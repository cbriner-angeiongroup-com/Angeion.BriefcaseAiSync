using BriefcaseSync.Infrastructure;
using BriefcaseSync.Web.Components;
using BriefcaseSync.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Azure.Identity;

bool checkSmartAdvocate = args.Length == 1 && args[0] == "--check-smartadvocate";
var builder = WebApplication.CreateBuilder(checkSmartAdvocate ? [] : args);
builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("BRIEFCASE_MAPPING_FILE") ?? Path.Combine(AppContext.BaseDirectory, "config/mapping.json"), optional: false)
    .AddUserSecrets(typeof(Program).Assembly, optional: true).AddEnvironmentVariables("BRIEFCASE_");
if (checkSmartAdvocate)
{
    try
    {
        var options = builder.Configuration.Get<AppOptions>() ?? new AppOptions();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = new SmartAdvocateClient(options.SmartAdvocate,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) });
        var count = await client.CheckConnection(timeout.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { @event = "smartadvocate_connection_passed", requestStatuses = count }));
    }
    catch
    {
        Console.Error.WriteLine("{\"event\":\"smartadvocate_connection_failed\",\"code\":\"CONFIGURATION_OR_DEPENDENCY_FAILURE\"}");
        Environment.ExitCode = 2;
    }
    return;
}
bool demo = builder.Configuration.GetValue<bool>("Web:DemoMode");
bool localPreview = builder.Configuration.GetValue<bool>("Web:LocalPreviewMode");
var defaults = builder.Configuration.Get<AppOptions>() ?? new AppOptions();
var mode = WorkspaceMode.Create(demo, localPreview, builder.Environment.IsDevelopment(), defaults);
if (localPreview)
{
    if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        throw new InvalidOperationException("Local preview must use its fixed loopback listener.");
    builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(5068));
}
builder.Logging.ClearProviders();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DetailedErrors = false)
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 256 * 1024); // Bounded room for the complete mapping editor.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(mode);
builder.Services.AddSingleton(defaults);
if (demo)
{
    builder.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>("Synthetic", _ => { });
    builder.Services.AddSingleton<IOperationsRepository, DemoOperationsRepository>();
}
else if (localPreview)
{
    builder.Services.AddAuthentication("LocalPreview").AddScheme<AuthenticationSchemeOptions, LocalPreviewAuthenticationHandler>("LocalPreview", _ => { });
    builder.Services.AddSingleton<IOperationsRepository, SqlOperationsRepository>();
}
else
{
    var keyBlob = builder.Configuration["Web:DataProtectionBlobUri"];
    var keyVaultKey = builder.Configuration["Web:DataProtectionKeyUri"];
    if (!Uri.TryCreate(keyBlob, UriKind.Absolute, out var blobUri) || blobUri.Scheme != "https"
        || !Uri.TryCreate(keyVaultKey, UriKind.Absolute, out var keyUri) || keyUri.Scheme != "https")
        throw new InvalidOperationException("Configure protected, persistent web Data Protection keys before live use.");
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = builder.Configuration["ManagedIdentityClientId"] });
    builder.Services.AddDataProtection().SetApplicationName("BriefcaseSync.Web/" + builder.Configuration["Scope"])
        .PersistKeysToAzureBlobStorage(blobUri, credential).ProtectKeysWithAzureKeyVault(keyUri, credential);
    var tenant = builder.Configuration["Entra:TenantId"]; var client = builder.Configuration["Entra:ClientId"];
    if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(client, out _) || string.IsNullOrWhiteSpace(builder.Configuration["Entra:ClientSecret"]))
        throw new InvalidOperationException("Configure Entra tenant, client ID and secret before starting the live UI.");
    builder.Services.AddAuthentication(o => { o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme; o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme; })
        .AddCookie(o =>
        {
            o.Cookie.Name = "__Host-BriefcaseSync"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(1); o.SlidingExpiration = false; o.AccessDeniedPath = "/auth/denied";
            o.Events.OnSigningIn = context =>
            {
                var expires = DateTimeOffset.UtcNow.AddHours(1); context.Properties.ExpiresUtc = expires;
                ((System.Security.Claims.ClaimsIdentity)context.Principal!.Identity!).AddClaim(new("sync_expires", expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(o =>
        {
            o.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0"; o.ClientId = client!; o.ClientSecret = builder.Configuration["Entra:ClientSecret"];
            o.ResponseType = "code"; o.UsePkce = true; o.SaveTokens = false; o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "name"; o.TokenValidationParameters.RoleClaimType = "roles";
            o.Events.OnRemoteFailure = context => { context.HandleResponse(); context.Response.Redirect("/auth/denied"); return Task.CompletedTask; };
        });
    builder.Services.AddSingleton<IOperationsRepository, SqlOperationsRepository>();
}
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("View", p => p.RequireAuthenticatedUser().RequireRole("Sync.View", "Sync.Preview", "Sync.Configure"));
    o.AddPolicy("Preview", p => p.RequireAuthenticatedUser().RequireRole("Sync.Preview"));
    o.AddPolicy("Configure", p => p.RequireAuthenticatedUser().RequireRole("Sync.Configure"));
});
builder.Services.AddScoped<OperationsService>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("Web:KnownProxies").Get<string[]>() ?? []) o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    foreach (var network in builder.Configuration.GetSection("Web:KnownProxyNetworks").Get<string[]>() ?? []) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});
var app = builder.Build();
if (localPreview)
    app.Use(async (context, next) =>
    {
        if (!WorkspaceMode.AllowsLocalRequest(context)) { context.Response.StatusCode = 403; return; }
        await next();
    });
else app.UseForwardedHeaders();
if (!demo && !localPreview) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseExceptionHandler(handler => handler.Run(async context => { context.Response.StatusCode = 500; await context.Response.WriteAsync("The request could not be completed. Please try again."); }));
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    await next();
});
app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/auth/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" })).AllowAnonymous();
app.MapGet("/auth/denied", () => Results.Content("Access is restricted. Ask your administrator for the appropriate sync role.", "text/plain")).AllowAnonymous();
app.MapPost("/auth/logout", async (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    if (demo || localPreview) return Results.LocalRedirect("/");
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization("View");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
