using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BriefcaseSync.Web.Services;

// An explicit Development-only grant to this machine's developer, never a deployed sign-in substitute.
public sealed class LocalPreviewAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, WorkspaceMode mode)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!mode.LocalPreview || !WorkspaceMode.AllowsLocalRequest(Context) || DateTimeOffset.UtcNow >= mode.SessionExpiresUtc)
            return Task.FromResult(AuthenticateResult.Fail("Local development session is unavailable. Restart the local preview host."));
        var claims = new[]
        {
            new Claim("oid", Environment.UserDomainName + "\\" + Environment.UserName),
            new Claim(ClaimTypes.Name, "Local developer"),
            new Claim("sync_expires", mode.SessionExpiresUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Role, "Sync.View"), new Claim(ClaimTypes.Role, "Sync.Preview"), new Claim(ClaimTypes.Role, "Sync.Configure")
        };
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
