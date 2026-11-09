using System.Net;
using System.Security.Claims;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using BriefcaseSync.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class LocalPreviewTests
{
    private static AppOptions Options() => new()
    {
        Scope = WorkspaceMode.LocalScope,
        SqlConnectionString = "Server=localhost;Initial Catalog=BriefcaseSync_LocalPreview;User ID=BriefcaseSync_LocalPreviewWeb;Password=synthetic;ConnectRetryCount=0;",
        StateEncryptionKey = Convert.ToBase64String(new byte[32]),
        SmartAdvocate = new() { ApiUrl = WorkspaceMode.DevelopmentApiUrl, PersonalAccessToken = "synthetic" },
        Mapping = Fixtures.Mapping()
    };

    [Fact]
    public void LocalPreviewCannotRunOutsideDevelopmentOrWithSyntheticMode()
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceMode.Create(false, true, false, Options()));
        Assert.Throws<InvalidOperationException>(() => WorkspaceMode.Create(true, true, true, Options()));
        Assert.True(WorkspaceMode.Create(false, true, true, Options()).LocalPreview);
    }

    [Fact]
    public void LocalPreviewRejectsOtherEndpointsSendingApprovalAndDatabaseIdentities()
    {
        foreach (var change in new Action<AppOptions>[]
        {
            o => o.SmartAdvocate.ApiUrl = "https://example.invalid/",
            o => o.SendingEnabled = true, o => o.MappingApproved = true,
            o => o.Scope = "another-scope",
            o => o.SqlConnectionString = "Server=remote;Initial Catalog=BriefcaseSync_LocalPreview;User ID=BriefcaseSync_LocalPreviewWeb;ConnectRetryCount=0;",
            o => o.SqlConnectionString = "Server=localhost;Initial Catalog=master;Integrated Security=True;ConnectRetryCount=0;",
            o => o.SqlConnectionString = "Server=localhost;Initial Catalog=BriefcaseSync_LocalPreview;User ID=BriefcaseSync_LocalPreviewWeb;ConnectRetryCount=1;"
        })
        {
            var options = Options(); change(options);
            Assert.Throws<InvalidOperationException>(() => WorkspaceMode.Create(false, true, true, options));
        }
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("::1", "localhost", true)]
    [InlineData("192.0.2.1", "localhost", false)]
    [InlineData("127.0.0.1", "example.invalid", false)]
    public void LocalPreviewChecksBothPeerAndHost(string address, string host, bool allowed)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Host = new(host, 5068);
        Assert.Equal(allowed, WorkspaceMode.AllowsLocalRequest(context));
    }

    [Fact]
    public async Task LocalPreviewRefusesEnablingEitherDeliveryGate()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("Configure", p => p.RequireRole("Sync.Configure")));
        using var provider = services.BuildServiceProvider(); var options = Options();
        var service = new OperationsService(new DemoOperationsRepository(options), options, new(false, true),
            new DeveloperState(), provider.GetRequiredService<IAuthorizationService>());
        foreach (var approval in new[] { false, true })
        {
            var settings = RuntimeSettings.From(options); settings.MappingApproved = approval; settings.SendingEnabled = !approval;
            await Assert.ThrowsAsync<UiOperationException>(() => service.Save(settings, 1));
        }
    }

    [Fact]
    public async Task ExpiredLocalCircuitCannotSaveSettings()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("Configure", p => p.RequireRole("Sync.Configure")));
        using var provider = services.BuildServiceProvider(); var options = Options();
        var service = new OperationsService(new DemoOperationsRepository(options), options, new(false, true),
            new DeveloperState(DateTimeOffset.UtcNow.AddSeconds(-1)), provider.GetRequiredService<IAuthorizationService>());
        var error = await Assert.ThrowsAsync<UiOperationException>(() => service.Save(RuntimeSettings.From(options), 1));
        Assert.Contains("session expired", error.Message);
    }

    [Fact]
    public void UnconfiguredPreviewShowsPartialValuesAndBlocksTheDeliveryPlan()
    {
        var preview = PreviewPlanner.Build("SYNTHETIC-1", Fixtures.Snapshot(), null, Fixtures.Mapping(), [],
            [new("configuration", "ELIGIBILITY_STATUS_REQUIRED", "Configure the eligibility status.")]);
        Assert.Equal("Validation blocked", preview.Action);
        Assert.Equal("Synthetic", preview.ProposedIntake!.ClientFirstName);
        var options = Options(); options.SmartAdvocate.ValidatePreview();
        Assert.Throws<InvalidOperationException>(options.SmartAdvocate.Validate);
    }

    private sealed class DeveloperState(DateTimeOffset? expires = null) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "synthetic-developer"), new Claim(ClaimTypes.Role, "Sync.Configure"),
                new Claim("sync_expires", (expires ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds().ToString())], "synthetic"))));
    }
}
