using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class WorkbookBindingTests
{
    [Fact]
    public void UnverifiedHelmetPartIsExplicitlyExcludedFromTheActiveProfile()
    {
        var configuration = Profile();
        var unresolved = new IntakeMapper(configuration.Mapping).SourcePaths()
            .Where(path => path.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(unresolved);
        configuration.Mapping.Validate();
        var helmet = Helmet(configuration.Mapping);
        Assert.False(helmet.Enabled);
        Assert.False(helmet.Required);
        Assert.False(string.IsNullOrWhiteSpace(helmet.DisabledReason));
        Assert.Equal("REPLACE_WITH_VERIFIED_SCREEN/MotorcycleApplicability", Assert.Single(helmet.WhenAll).UdfPath);
        Assert.DoesNotContain(helmet.UdfPath, new IntakeMapper(configuration.Mapping).SourcePaths());
        Assert.False(configuration.MappingApproved);
        Assert.False(configuration.SendingEnabled);
        Assert.False(configuration.SmartAdvocate.TestTrigger.Enabled);
        Assert.True(configuration.Mapping.DescriptionEnabled);
        Assert.True(configuration.Mapping.DescriptionContractVerified);
    }

    [Fact]
    public void WorkbookBindingsPreserveLeadIdAndIncidentDateAndSeparateInsuranceClaims()
    {
        var mapping = Profile().Mapping;
        var source = SyntheticCase();
        source.IncidentDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Answer(source, "LAW-TRIGERS-ID", "000123");
        Answer(source, "GEN-ACCIDENT-DATE", "2026-05-17");
        Answer(source, "INSURANCE-COVERAGE", "Yes");
        Answer(source, "INSURANCE-CLAIM-FILED", "Yes");
        Answer(source, "INSURANCE-CLAIM-NO", "SYNTHETIC-CLIENT-CLAIM");
        Answer(source, "FAULT-INSURED", "Yes");
        Answer(source, "FAULT-INSURANCE-CLAIM", "Yes");
        Answer(source, "FAULT-INSURANCE-CLAIM-NO", "SYNTHETIC-OTHER-CLAIM");
        var request = new IntakeMapper(mapping).Map(source);
        Assert.Equal("000123", request.LeadVendorId);
        Assert.Equal("Automobile Accident", request.CaseType);
        Assert.Equal("2026-05-17", request.IncidentDate);
        Assert.Contains("SYNTHETIC-CLIENT-CLAIM", request.Notes);
        Assert.DoesNotContain("SYNTHETIC-OTHER-CLAIM", request.Notes);
        Assert.Contains("SYNTHETIC-OTHER-CLAIM", request.DefendantInformation);
        Assert.DoesNotContain("SYNTHETIC-CLIENT-CLAIM", request.DefendantInformation);
        Assert.Null(request.Injured);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("No")]
    public void VehicleAnswersAreIncludedWhileHelmetIsSeparatelyBlocked(string helmet)
    {
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Answer(source, "VEHICLE-DETAILS", "Synthetic vehicle");
        Answer(source, "VEHICLE-DAMAGE", "Yes");
        Answer(source, "VEHICLE-DAMAGE-DETAILS", "Synthetic vehicle damage");
        Answer(source, "VEHICLE-TOWING", "No");
        Answer(source, "VEHICLE-HELMET", helmet);
        var mapping = Profile().Mapping;
        var request = new IntakeMapper(mapping).Map(source);
        Assert.Contains("Synthetic vehicle", request.VehicleInformation);
        Assert.Contains("Synthetic vehicle damage", request.VehicleInformation);
        Assert.Contains("Was your vehicle towed?: No", request.VehicleInformation);
        Assert.DoesNotContain("helmet", request.VehicleInformation!, StringComparison.OrdinalIgnoreCase);
        var preview = PreviewPlanner.Build("SYNTHETIC-1", new(source, true, null), null, mapping, []);
        Assert.Equal("Create intake", preview.Action);
        Assert.Empty(preview.Errors);
        var vehicle = Assert.Single(preview.Fields, field => field.Key == "vehicleInformation");
        Assert.Equal("Included", vehicle.Outcome);
        Assert.Equal("4 labeled answers", vehicle.Source);
        Assert.Equal(request.VehicleInformation, vehicle.Value);
        var blocked = Assert.Single(preview.Fields, field => field.Key == "vehicleInformation/FB-U075");
        Assert.Equal("Blocked", blocked.Outcome);
        Assert.Null(blocked.Value);
        Assert.Contains("Motorcycle applicability is not verified", blocked.Source);
    }

    [Fact]
    public void ReenablingHelmetStillRequiresVerifiedApplicability()
    {
        var mapping = Profile().Mapping; Helmet(mapping).Enabled = true;
        Assert.Throws<InvalidOperationException>(mapping.Validate);
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Answer(source, "VEHICLE-HELMET", "No");
        var error = Assert.Throws<IntakeValidationException>(() => new IntakeMapper(mapping).Map(source));
        Assert.Contains(error.Errors, item => item.Field.StartsWith("vehicleInformation/", StringComparison.Ordinal)
            && item.Code == "MAPPING_ERROR");
    }

    [Theory]
    [InlineData(true, "Pending verification")]
    [InlineData(false, "")]
    public void DisabledPartCannotHideRequiredAnswersOrOmitItsReason(bool required, string reason)
    {
        var mapping = Profile().Mapping;
        Helmet(mapping).Required = required; Helmet(mapping).DisabledReason = reason;
        Assert.Throws<InvalidOperationException>(mapping.Validate);
        var source = SyntheticCase(); Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Assert.Throws<IntakeValidationException>(() => new IntakeMapper(mapping).Map(source));
    }

    [Fact]
    public void DisablingAnEntireDestinationIsRejected()
    {
        var mapping = Profile().Mapping;
        mapping.Mappings.Single(m => m.Destination == "caseType").Enabled = false;
        Assert.Throws<InvalidOperationException>(mapping.Validate);
    }

    [Fact]
    public void ActiveVehicleErrorsStillBlockTheVehicleField()
    {
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Answer(source, "VEHICLE-DETAILS", "Synthetic vehicle");
        Answer(source, "VEHICLE-DAMAGE", "Untranslated synthetic choice");
        var error = Assert.Throws<IntakeValidationException>(() => new IntakeMapper(Profile().Mapping).Map(source));
        Assert.Contains(error.Errors, item => item.Field.StartsWith("vehicleInformation/", StringComparison.Ordinal));
        Assert.Null(error.PartialRequest!.VehicleInformation);
    }

    [Fact]
    public void FrozenRetryDoesNotClaimTheCurrentHelmetExclusionChangedItsPayload()
    {
        var mapping = Profile().Mapping;
        var source = SyntheticCase(); Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        var frozen = new IntakeMapper(mapping).Map(source);
        frozen.VehicleInformation = "Synthetic previously frozen vehicle text";
        var preview = PreviewPlanner.Build("SYNTHETIC-1", new(source, true, null),
            new() { SourceCaseId = source.Id, LeadVendorId = frozen.LeadVendorId, SubmissionRequest = frozen }, mapping, []);
        Assert.Equal("Retry frozen intake", preview.Action);
        Assert.Equal(frozen.VehicleInformation, preview.ProposedIntake!.VehicleInformation);
        Assert.DoesNotContain(preview.Fields, field => field.Key == "vehicleInformation/FB-U075");
    }

    private static FieldMapping Helmet(MappingOptions mapping) => mapping.Mappings.Single(m => m.Destination == "vehicleInformation")
        .Parts.Single(p => p.InventoryId == "FB-U075");

    [Fact]
    public void FreeTextPlaintiffDetailsNeverBecomeAGuardianAndDoNotOverrideTheIncidentDate()
    {
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Answer(source, "GEN-ACCIDENT-DATE", "05/17/2026");
        const string narrative = "Synthetic incident occurred on April 12, 2026.";
        Answer(source, "GEN-ACCIDENT-SUMMARY", narrative);
        Answer(source, "PLAINTIFF-DETAILS", narrative);
        source.PrimaryContactAddressCountryName = "UNITED STATES OF AMERICA";

        var mapping = Profile().Mapping;
        var request = new IntakeMapper(mapping).Map(source);

        Assert.Equal("2026-05-17", request.IncidentDate);
        Assert.Contains("Incident summary: " + narrative, request.Notes);
        Assert.Contains("Plaintiff details: " + narrative, request.Notes);
        Assert.Null(request.ClientGuardianPerson);
        Assert.Equal("UNITED STATES OF AMERICA", request.ClientBillingCountry);
        Assert.DoesNotContain("clientGuardianPerson", request.Fields().Keys);
        Assert.DoesNotContain("description", request.Fields().Keys);
        Assert.True(mapping.DescriptionEnabled);
        Assert.True(mapping.DescriptionContractVerified);
    }

    [Fact]
    public void GuardianCorrectionPreservesFrozenRequestsAndFlagsAcknowledgedIntakesForReview()
    {
        var mapping = Profile().Mapping;
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        Answer(source, "PLAINTIFF-DETAILS", "Synthetic plaintiff narrative");
        var frozen = new IntakeMapper(mapping).Map(source);
        frozen.ClientGuardianPerson = "Synthetic previously frozen narrative";
        var state = new SyncState
        {
            SourceCaseId = source.Id, LeadVendorId = frozen.LeadVendorId,
            SubmissionRequest = frozen, MappingVersion = "synthetic-previous-profile",
            Fingerprint = IntakeDelivery.Hash(JsonSerializer.Serialize(frozen, IntakeRequest.JsonOptions))
        };
        var snapshot = new SourceSnapshot(source, true, null);

        var retry = PreviewPlanner.Build("SYNTHETIC-1", snapshot, state, mapping, []);
        Assert.Equal("Retry frozen intake", retry.Action);
        Assert.Same(frozen, retry.ProposedIntake);
        Assert.Equal("synthetic-previous-profile", retry.MappingVersion);
        Assert.Equal(frozen.ClientGuardianPerson, retry.ProposedIntake!.ClientGuardianPerson);

        state.IntakeId = "synthetic-intake";
        state.SubmissionRequest = null;
        var acknowledged = PreviewPlanner.Build("SYNTHETIC-1", snapshot, state, mapping, []);
        Assert.Equal("Update required", acknowledged.Action);
        Assert.Null(acknowledged.ProposedIntake!.ClientGuardianPerson);
        Assert.Contains("Plaintiff details: Synthetic plaintiff narrative", acknowledged.ProposedIntake.Notes);
    }

    [Fact]
    public void MiddleNameIsALabeledNoteWithoutChangingFirstNameOrInventingAnApiKey()
    {
        var source = SyntheticCase();
        Answer(source, "LAW-TRIGERS-ID", "TIGER-SYNTHETIC");
        source.SourceFields["PrimaryContactMiddleName"] = "Synthetic Middle";
        var request = new IntakeMapper(Profile().Mapping).Map(source);
        Assert.Contains("Primary contact middle name: Synthetic Middle", request.Notes);
        Assert.Equal(source.PrimaryContactNameFirst, request.ClientFirstName);
        Assert.DoesNotContain("clientMiddleName", request.Fields().Keys);
    }

    private static AppOptions Profile() => JsonSerializer.Deserialize<AppOptions>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/mapping.json")))!;

    private static SourceCase SyntheticCase()
    {
        var source = Fixtures.Snapshot().Case;
        source.CustomFields.Clear();
        source.CaseType = "MVA-FB-SE";
        source.SourceFields = new()
        {
            ["PrimaryContactMobilePhone"] = null, ["PrimaryContactMaritalStatus"] = null,
            ["PrimaryContactMaidenName"] = null, ["PrimaryContactSpouseName"] = null,
            ["PrimaryContactLanguages"] = null, ["PrimaryContactDriversLicenseNumber"] = null,
            ["PrimaryContactEmergencyContact"] = null, ["PrimaryContactEmergencyPhone"] = null,
            ["PrimaryContactSmsOptOut"] = null, ["PrimaryContactSubcategory"] = null,
            ["PrimaryContactDeceased"] = null,
            ["PrimaryContactMiddleName"] = null
        };
        return source;
    }

    private static void Answer(SourceCase source, string shortName, string value) =>
        source.CustomFields.Add(new() { Path = "Case Incident/" + shortName, Enabled = true, FieldValue = value });
}
