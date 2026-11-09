using Azure.Identity;
using Azure.Storage.Blobs;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Microsoft.Extensions.Configuration;

try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile(Environment.GetEnvironmentVariable("BRIEFCASE_MAPPING_FILE") ?? "config/mapping.json", optional: false)
        .AddUserSecrets(typeof(Program).Assembly, optional: true)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddEnvironmentVariables("BRIEFCASE_")
        .Build();
    var options = configuration.Get<AppOptions>() ?? throw new InvalidOperationException("Missing configuration.");
    if (args.Length == 1 && args[0] == "--check-smartadvocate")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = new SmartAdvocateClient(options.SmartAdvocate,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) });
        var count = await client.CheckConnection(timeout.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { @event = "smartadvocate_connection_passed", requestStatuses = count }));
        return 0;
    }
    bool validateOnly = args.Length == 1 && args[0] == "--validate-config";
    if (options.ManagedConfigurationEnabled && !validateOnly)
    {
        var saved = await new SqlOperationsRepository(options).LoadSettings(options, CancellationToken.None);
        saved.Settings.Apply(options);
        options.ConfigurationVersion = saved.Version;
        options.ConfigurationSavedAtUtc = saved.SavedAtUtc;
    }
    options.Validate();
    if (args.Length > 0 && args[0] == "--validate-config")
    {
        Console.WriteLine("{\"event\":\"configuration_valid\"}");
        return 0;
    }
    bool reconcile = args.Length == 2 && args[0] == "--reconcile-upload";
    if (args.Length > 0 && !reconcile) throw new InvalidOperationException("Unsupported command.");
    if (reconcile && !options.SendingEnabled) throw new InvalidOperationException("Reconciliation requires delivery configuration.");
    using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(options.RunTimeoutMinutes));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
    using var sa = new SmartAdvocateClient(options.SmartAdvocate, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(3) });
    using var briefcase = new BriefCaseAiApiClient(options.Briefcase);
    var store = new SqlSyncStore(options);
    var source = new SmartAdvocateSource(sa, options.SmartAdvocate, options.Mapping);
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = options.ManagedIdentityClientId });
    // Dry-run needs no Blob or vendor credentials. It reads source and SQL lock only and never persists delivery state.
    var assets = new BlobRetainerAssets(new BlobContainerClient(new Uri(string.IsNullOrWhiteSpace(options.BlobServiceUri)
        ? "https://unused.blob.core.windows.net/retainer-assets" : options.BlobServiceUri.TrimEnd('/') + "/" + options.AssetContainer), credential), options.Scope);
    var intake = new IntakeDelivery(options.Mapping, store, new BriefcaseDestination(briefcase));
    if (reconcile)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<UploadReconciliationRequest>(await File.ReadAllTextAsync(args[1], shutdown.Token))
            ?? throw new InvalidOperationException("Missing reconciliation request.");
        await new UploadReconciliation(store, assets, briefcase).Reconcile(request, shutdown.Token);
        Console.WriteLine("{\"event\":\"upload_reconciled\"}");
        return 0;
    }
    var retainers = new RetainerDelivery(source, store, assets, briefcase, options.SmartAdvocate.RetainersEnabled, options.DocumentTypeMappings);
    var result = await new SyncRunner(source, store, intake, retainers, new SqlRunJournal(options, options.ManagedConfigurationEnabled && options.ConfigurationVersion > 0)).Run(options.SendingEnabled, shutdown.Token);
    // Deliberate allowlist: aggregate counts only, no automatic HTTP/SQL instrumentation or raw exception logging.
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { @event = "sync_finished", dryRun = !options.SendingEnabled, discovered = result.Discovered, processed = result.Processed, failed = result.Failed, attention = result.Attention, overlapSkipped = result.OverlapSkipped, scheduleSkipped = result.ScheduleSkipped }));
    return result.Failed > 0 || result.Attention > 0 ? 1 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("{\"event\":\"sync_stopped\",\"code\":\"CANCELLED_OR_TIMED_OUT\"}");
    return 2;
}
catch
{
    Console.Error.WriteLine("{\"event\":\"sync_failed\",\"code\":\"CONFIGURATION_OR_DEPENDENCY_FAILURE\"}");
    return 2;
}
