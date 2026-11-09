using System.Security.Claims;
using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using BriefcaseSync.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class PreviewTests
{
    [Fact]
    public void PreviewMatchesTheActualIntakePayloadAndHasNoWriteDependency()
    {
        var snapshot = Fixtures.Snapshot(); var mapping = Fixtures.Mapping();
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, null, mapping, [new(7, "synthetic.pdf", "Signed Retainer Agreement")]);
        Assert.Equal("Create intake", preview.Action);
        Assert.Equal(JsonSerializer.Serialize(new IntakeMapper(mapping).Map(snapshot.Case)), JsonSerializer.Serialize(preview.ProposedIntake));
        Assert.Equal("After intake creation", Assert.Single(preview.Documents).Action);
        Assert.Equal("Signed Retainer Agreement", preview.Documents[0].SourceType);
        Assert.Equal("Signed Retainer Agreement", preview.Documents[0].DocumentType);
    }

    [Fact]
    public void DocumentPreviewUsesConfiguredMappingAndShowsUnmappedTypesAsBlocked()
    {
        var preview = PreviewPlanner.Build("SYNTHETIC-1", Fixtures.Snapshot(), null, Fixtures.Mapping(),
            [new(7, "synthetic.pdf", "SA retainer"), new(8, "synthetic-unknown.pdf", null)],
            documentTypeMappings: [new() { SourceType = "SA retainer", DocumentType = "Signed Retainer Agreement" }]);
        Assert.Equal("Signed Retainer Agreement", preview.Documents[0].DocumentType);
        Assert.Equal("Document type mapping required", preview.Documents[1].Action);
        Assert.Null(preview.Documents[1].DocumentType);
    }

    [Fact]
    public void PendingDocumentPreviewShowsFrozenTypeInsteadOfChangedMapping()
    {
        var state = new SyncState { SourceCaseId = 1, IntakeId = "intake-1", LeadVendorId = "TIGER-SYNTHETIC-1" };
        state.Documents["synthetic"] = new()
        {
            SourceDocumentId = 7, FileName = "synthetic.pdf", SourceDocumentType = "SA retainer", Status = "Initializing",
            InitializationRequest = new("intake-1", "TIGER-SYNTHETIC-1", "Signed Retainer Agreement", "synthetic.pdf", 1)
        };
        var preview = PreviewPlanner.Build("SYNTHETIC-1", Fixtures.Snapshot(), state, Fixtures.Mapping(),
            [new(7, "synthetic-changed.pdf", "SA retainer")],
            documentTypeMappings: [new() { SourceType = "SA retainer", DocumentType = "Other" }]);
        var document = Assert.Single(preview.Documents);
        Assert.Equal("Signed Retainer Agreement", document.DocumentType);
        Assert.Equal("Reconciliation required", document.Action);
    }
    [Fact]
    public void InvalidPreviewRetainsValidFieldsAndExplainsFailures()
    {
        var snapshot = Fixtures.Snapshot(); snapshot.Case.PrimaryContactEmailAddress = null;
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, null, Fixtures.Mapping(), []);
        Assert.Equal("Validation blocked", preview.Action);
        Assert.Equal("Synthetic", preview.ProposedIntake!.ClientFirstName);
        Assert.Contains(preview.Errors, e => e.Field == "clientEmail/clientPhone");
    }
    [Fact]
    public void EmptyRequiredFieldsHaveOneIssueEachAndAreNotInvalidReferenceValues()
    {
        var snapshot = Fixtures.Snapshot();
        snapshot.Case.PrimaryContactNameFirst = null; snapshot.Case.PrimaryContactNameLast = null;
        snapshot.Case.CustomFields.RemoveAll(f => f.Path == "Incident/Type");
        var mapping = Fixtures.Mapping(); mapping.Mappings.Single(m => m.Destination == "caseType").Required = true;
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, null, mapping, []);
        Assert.Equal("Validation blocked", preview.Action);
        foreach (var field in new[] { "clientFirstName", "clientLastName", "caseType" })
            Assert.Single(preview.Errors, e => e.Field == field);
        Assert.DoesNotContain(preview.Errors, e => e.Code == "INVALID_CASE_TYPE");
        var invalid = new IntakeMapper(Fixtures.Mapping()).Map(Fixtures.Snapshot().Case);
        invalid.CaseType = "Unsupported synthetic type";
        Assert.Contains(new IntakeValidator(mapping).Validate(invalid), e => e.Code == "INVALID_CASE_TYPE");
    }

    [Fact]
    public void PreviewShowsFrozenRetryRatherThanNewAnswers()
    {
        var snapshot = Fixtures.Snapshot(); var mapping = Fixtures.Mapping();
        var frozen = new IntakeMapper(mapping).Map(snapshot.Case); snapshot.Case.PrimaryContactNameFirst = "Changed";
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, new() { SourceCaseId = 1, LeadVendorId = frozen.LeadVendorId, SubmissionRequest = frozen }, mapping, []);
        Assert.Equal("Retry frozen intake", preview.Action); Assert.Equal("Synthetic", preview.ProposedIntake!.ClientFirstName);
    }
    [Fact]
    public void PreviewDoesNotDescribeAnUnsupportedUpdateAsDelivery()
    {
        var snapshot = Fixtures.Snapshot(); var mapping = Fixtures.Mapping();
        var previous = new IntakeMapper(mapping).Map(snapshot.Case);
        snapshot.Case.PrimaryContactNameFirst = "Changed";
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, new() { SourceCaseId = 1, LeadVendorId = previous.LeadVendorId, IntakeId = "existing", Fingerprint = IntakeDelivery.Hash(JsonSerializer.Serialize(previous, IntakeRequest.JsonOptions)) }, mapping, []);
        Assert.Equal("Update required", preview.Action);
    }
    [Fact]
    public async Task ViewerCannotPreviewRevealOrChangeConfiguration()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddAuthorization(o =>
        {
            o.AddPolicy("Preview", p => p.RequireRole("Sync.Preview")); o.AddPolicy("Configure", p => p.RequireRole("Sync.Configure"));
        });
        using var provider = services.BuildServiceProvider();
        var defaults = new AppOptions { Mapping = Fixtures.Mapping() };
        var operations = new OperationsService(new DemoOperationsRepository(defaults), defaults, new(true), new ViewerState(), provider.GetRequiredService<IAuthorizationService>());
        await Assert.ThrowsAsync<UiOperationException>(() => operations.Preview("SA-10428"));
        await Assert.ThrowsAsync<UiOperationException>(() => operations.RecordDisclosure(1));
        await Assert.ThrowsAsync<UiOperationException>(() => operations.Save(RuntimeSettings.From(defaults), 1));
    }
    [Theory]
    [InlineData("not cron")]
    [InlineData("0 0 31 2 *")]
    public void InvalidOrImpossibleSchedulesAreRejected(string cron)
    {
        var settings = new RuntimeSettings { ScheduleCron = cron, Mapping = Fixtures.Mapping() };
        Assert.ThrowsAny<Exception>(settings.Validate);
    }
    [Fact]
    public async Task UnsavedChangesCannotMutateDefaultsOrPublishedSettings()
    {
        var defaults = new AppOptions { Mapping = Fixtures.Mapping() };
        var draft = RuntimeSettings.From(defaults); draft.Mapping.MappingVersion = "unsaved";
        Assert.Equal("synthetic-v1", defaults.Mapping.MappingVersion);
        var repository = new DemoOperationsRepository(defaults);
        var saved = await repository.LoadSettings(defaults, default);
        await repository.SaveSettings(saved.Settings, saved.Version, "synthetic", default);
        saved.Settings.Mapping.MappingVersion = "changed-after-save";
        Assert.NotEqual("changed-after-save", (await repository.LoadSettings(defaults, default)).Settings.Mapping.MappingVersion);
    }
    private sealed class ViewerState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "synthetic-viewer"), new Claim(ClaimTypes.Role, "Sync.View")], "synthetic"))));
    }
}
