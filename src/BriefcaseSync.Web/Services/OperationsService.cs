using System.Security.Claims;
using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace BriefcaseSync.Web.Services;

public sealed class UiOperationException(string message) : Exception(message);
public sealed class OperationsService(IOperationsRepository repository, AppOptions defaults, WorkspaceMode mode,
    AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public bool IsDemo => mode.Demo;
    public bool IsLocalPreview => mode.LocalPreview;
    public bool ManagedConfiguration => mode.Demo || defaults.ManagedConfigurationEnabled;
    private async Task<string> Authorize(string policy)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!mode.Demo && (!long.TryParse(user.FindFirstValue("sync_expires"), out var expiry) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry))
            throw new UiOperationException("Your session expired. Sign in again before continuing.");
        if (!(await authorization.AuthorizeAsync(user, null, policy)).Succeeded) throw new UiOperationException("Your role does not permit this action.");
        return user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UiOperationException("An auditable user identity is required.");
    }
    public async Task<SettingsSnapshot> Settings(CancellationToken ct = default)
    {
        await Authorize("View");
        try { return await repository.LoadSettings(defaults, ct); }
        catch { throw new UiOperationException("Settings are unavailable. Check the application's database connection and schema deployment."); }
    }
    public async Task<OperationsDashboard> Dashboard(CancellationToken ct = default)
    {
        await Authorize("View");
        try { return await repository.Dashboard(ct); }
        catch { throw new UiOperationException("Sync details are unavailable. Check the application's database connection and schema deployment."); }
    }
    public async Task<SettingsSnapshot> Save(RuntimeSettings settings, int version, CancellationToken ct = default)
    {
        var actor = await Authorize("Configure");
        if (mode.LocalPreview && (settings.SendingEnabled || settings.MappingApproved))
            throw new UiOperationException("Local development preview keeps sending and mapping approval disabled.");
        if (!ManagedConfiguration) throw new UiOperationException("Enable managed configuration on the worker and web host before editing runtime settings.");
        try { DocumentTypes.Validate(settings.DocumentTypeMappings); }
        catch { throw new UiOperationException("Check document type mappings: each SmartAdvocate type must be unique and have one of the six supported BriefCase types. Blank values are not allowed."); }
        try { settings.Validate(); }
        catch { throw new UiOperationException("Check the UTC cron, status ID, document IDs, timeout and mapping approval. Sending requires a complete approved mapping."); }
        try { return await repository.SaveSettings(settings, version, actor, ct); }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number == 51000) { throw new UiOperationException("Settings changed since you opened them. Reload before saving."); }
        catch (UiOperationException) { throw; }
        catch { throw new UiOperationException("Settings could not be saved. Reload to check the current version before trying again."); }
    }
    public async Task<SyncPreview> Preview(string caseNumber, CancellationToken ct = default)
    {
        var actor = await Authorize("Preview");
        if (string.IsNullOrWhiteSpace(caseNumber) || caseNumber.Length > 100) throw new UiOperationException("Enter an exact SmartAdvocate case number.");
        try
        {
            var saved = await repository.LoadSettings(defaults, ct);
            var effective = JsonSerializer.Deserialize<AppOptions>(JsonSerializer.Serialize(defaults))!; saved.Settings.Apply(effective);
            if (mode.LocalPreview && (effective.SendingEnabled || effective.MappingApproved))
                throw new UiOperationException("Disable sending and mapping approval in the local preview configuration before continuing.");
            var configurationErrors = new List<FieldError>();
            SourceSnapshot snapshot; IReadOnlyList<SourceDocument> documents; long id;
            if (mode.Demo)
            {
                if (caseNumber.Trim() != "SA-10428") throw new UiOperationException("This synthetic workspace contains case SA-10428. Live case lookup requires a configured connection.");
                snapshot = DemoData.Snapshot(effective.Mapping); id = snapshot.Case.Id;
                documents = effective.SmartAdvocate.RetainersEnabled ? [new(701, "Signed retainer — synthetic client.pdf", "Signed Retainer Agreement")] : [];
            }
            else
            {
                effective.SmartAdvocate.ValidatePreview();
                if (effective.Mapping.DescriptionEnabled && !effective.Mapping.DescriptionContractVerified)
                    throw new UiOperationException("Verify additional-person description support before enabling it.");
                try { effective.Mapping.Validate(); }
                catch (InvalidOperationException)
                {
                    var unresolved = new IntakeMapper(effective.Mapping).SourcePaths()
                        .Where(path => path.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
                    configurationErrors.Add(new("configuration", "MAPPING_CONFIGURATION_REQUIRED", unresolved.Length > 0
                        ? "Verify these unresolved UDF source bindings before delivery: " + string.Join(", ", unresolved)
                        : "Verify the mapping version, Tiger Lead ID path and source bindings before delivery."));
                }
                if (effective.SmartAdvocate.EligibleStatusId <= 0)
                    configurationErrors.Add(new("configuration", "ELIGIBILITY_STATUS_REQUIRED", "Configure the approved eligibility status ID before assessing delivery eligibility."));
                using var client = new SmartAdvocateClient(effective.SmartAdvocate, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(2) });
                var source = new SmartAdvocateSource(client, effective.SmartAdvocate, effective.Mapping);
                id = await source.FindCaseNumber(caseNumber, ct);
                await repository.Audit(actor, "PreviewRequested", id, ct);
                var loaded = await source.LoadPreview(id, ct);
                snapshot = loaded.Snapshot; configurationErrors.AddRange(loaded.Errors);
                documents = await source.Retainers(id, ct);
            }
            var result = PreviewPlanner.Build(caseNumber.Trim(), snapshot, await repository.FindState(id, ct), effective.Mapping, documents, configurationErrors, effective.DocumentTypeMappings);
            await repository.Audit(actor, "PreviewGenerated", id, ct); return result;
        }
        catch (UiOperationException) { throw; }
        catch { throw new UiOperationException("Preview could not load an exact case. Verify the case number, source connection, UDF bindings and access permissions. Nothing was sent."); }
    }
    public async Task RecordDisclosure(long caseId, CancellationToken ct = default)
    {
        var actor = await Authorize("Preview");
        try { await repository.Audit(actor, "PreviewDisclosed", caseId, ct); }
        catch { throw new UiOperationException("Access could not be recorded. Protected values remain hidden."); }
    }
}
