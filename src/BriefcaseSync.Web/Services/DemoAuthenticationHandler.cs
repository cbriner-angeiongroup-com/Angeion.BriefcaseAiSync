using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BriefcaseSync.Web.Services;

public sealed class DemoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim("oid", "synthetic-developer"), new Claim(ClaimTypes.Name, "Development workspace"),
            new Claim(ClaimTypes.Role, "Sync.View"), new Claim(ClaimTypes.Role, "Sync.Preview"), new Claim(ClaimTypes.Role, "Sync.Configure") };
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
