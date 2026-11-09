using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class ContactFieldTests
{
    [Theory]
    [InlineData(0, "Female")]
    [InlineData(1, "Male")]
    public async Task ConfiguredContactChoicesReachThePreviewWithoutOmission(long genderId, string expectedGender)
    {
        var configuration = JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/mapping.json")))!;
        var options = Options();
        options.GenderValues = configuration.SmartAdvocate.GenderValues;
        options.MaritalStatusValues = configuration.SmartAdvocate.MaritalStatusValues;
        using var client = Client(options, genderId, maritalStatusId: 1);
        var mapping = Fixtures.Mapping();
        mapping.Mappings.Add(new() { StandardProperty = "PrimaryContactPhoneNumber", Destination = "clientPhone" });
        mapping.Mappings.Add(new() { StandardProperty = "PrimaryContactGender", Destination = "clientGender" });
        mapping.Mappings.Add(new() { SourceField = "PrimaryContactMaritalStatus", Destination = "clientMaritalStatus" });
        var snapshot = await new SmartAdvocateSource(client, options, mapping).LoadPreview(1, default);
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot.Snapshot, null, mapping, [], snapshot.Errors);
        Assert.Empty(preview.Errors);
        Assert.Equal(expectedGender, preview.ProposedIntake!.ClientGender);
        Assert.Equal("Married", preview.ProposedIntake.ClientMaritalStatus);
        Assert.Equal("Included", Assert.Single(preview.Fields, f => f.Key == "clientGender").Outcome);
        Assert.Equal("Included", Assert.Single(preview.Fields, f => f.Key == "clientMaritalStatus").Outcome);
    }

    [Fact]
    public void NumericChoicesRequireVerifiedTranslationsIncludingZero()
    {
        Assert.Null(ContactFieldRules.Choice(null, null, new Dictionary<long, string>(), "clientGender"));
        Assert.Equal("Female", ContactFieldRules.Choice(" ", 0, new Dictionary<long, string> { [0] = "Female" }, "clientGender"));
        Assert.Equal("Male", ContactFieldRules.Choice(" Male ", 91, new Dictionary<long, string>(), "clientGender"));
        Assert.Null(ContactFieldRules.Choice(null, 0, new Dictionary<long, string> { [0] = "" }, "clientGender"));
        var error = Assert.Throws<IntakeValidationException>(() => ContactFieldRules.Choice(null, 0, new Dictionary<long, string>(), "clientGender"));
        Assert.Equal("SOURCE_CHOICE_BINDING_REQUIRED", Assert.Single(error.Errors).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyPrefixIsExplicitAndMiddleAndSpouseFactsArePreserved(bool configurePrefix)
    {
        var options = Options(); options.EmergencyPhoneLabelPrefix = configurePrefix ? "EC:" : "";
        options.GenderValues[0] = "Female"; options.MaritalStatusValues[55] = "Married";
        using var client = Client(options, genderId: 0, maritalStatusId: 55);
        var snapshot = await new SmartAdvocateSource(client, options, Fixtures.Mapping()).Load(1, default);
        Assert.Equal("Female", snapshot.Case.PrimaryContactGender);
        Assert.Equal("Married", snapshot.Case.SourceFields["PrimaryContactMaritalStatus"]);
        Assert.Equal("Synthetic Middle", snapshot.Case.SourceFields["PrimaryContactMiddleName"]);
        Assert.Equal("Synthetic Spouse", snapshot.Case.SourceFields["PrimaryContactSpouseName"]);
        Assert.Equal(configurePrefix ? "Synthetic Contact" : null, snapshot.Case.SourceFields["PrimaryContactEmergencyContact"]);
        Assert.Equal(configurePrefix ? "5551112222" : null, snapshot.Case.SourceFields["PrimaryContactEmergencyPhone"]);
    }

    [Fact]
    public async Task UnmappedSuppliedChoiceIsExplainedInPreviewAndBlocksStrictDelivery()
    {
        var options = Options(); options.MaritalStatusValues[55] = "Married";
        using var client = Client(options, genderId: 0, maritalStatusId: 55);
        var mapping = Fixtures.Mapping(); mapping.Mappings.Add(new() { StandardProperty = "PrimaryContactGender", Destination = "clientGender" });
        var source = new SmartAdvocateSource(client, options, mapping);
        var preview = await source.LoadPreview(1, default);
        Assert.Contains(preview.Errors, e => e.Field == "clientGender" && e.Code == "SOURCE_CHOICE_BINDING_REQUIRED");
        Assert.Equal("Synthetic", preview.Snapshot.Case.PrimaryContactNameFirst);
        var plan = PreviewPlanner.Build("SYNTHETIC-1", preview.Snapshot, null, mapping, [], preview.Errors);
        Assert.Equal("Blocked", Assert.Single(plan.Fields, f => f.Key == "clientGender").Outcome);
        await Assert.ThrowsAsync<IntakeValidationException>(() => source.Load(1, default));
    }

    [Fact]
    public async Task ExactAndPrefixedEmergencyCandidatesAreNotSilentlyCollapsed()
    {
        var options = Options(); options.EmergencyPhoneLabelPrefix = "EC:";
        options.GenderValues[0] = "Female"; options.MaritalStatusValues[55] = "Married";
        using var client = Client(options, 0, 55, duplicateEmergency: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SmartAdvocateSource(client, options, Fixtures.Mapping()).Load(1, default));
    }

    [Fact]
    public void EmergencyCommentsTakePrecedenceAndNoRelationshipIsInvented()
    {
        Assert.Equal("Synthetic Person; Parent", ContactFieldRules.EmergencyContact(" Synthetic Person; Parent ", "EC: Synthetic Person", "EC:"));
        Assert.Equal("Synthetic Person", ContactFieldRules.EmergencyContact(null, " EC: Synthetic Person ", "EC:"));
        Assert.Null(ContactFieldRules.EmergencyContact(null, "Emergency", "EC:"));
    }

    private static SmartAdvocateOptions Options() => new() { ApiUrl = "https://sa.example.invalid/CaseSyncAPI/", PersonalAccessToken = "synthetic", EligibleStatusId = 321 };

    private static SmartAdvocateClient Client(SmartAdvocateOptions options, long genderId, long maritalStatusId, bool duplicateEmergency = false) =>
        new(options, new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var path when path.Contains("authenticate") => Handler.Json(new { token = "synthetic-token" }),
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[] { new { id = 10, contactID = 100, primaryContact = true } }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(new
            {
                contactID = 100, firstName = "Synthetic", lastName = "Person", middleName = "Synthetic Middle", spouse = "Synthetic Spouse",
                genderID = genderId, maritalStatusID = maritalStatusId, active = true,
                phones = new[]
                {
                    new { phoneID = 1, name = "Cell", phoneNumber = "5551234567", primary = true },
                    new { phoneID = 2, name = "EC: Synthetic Contact", phoneNumber = "5551112222", primary = false }
                }.Concat(duplicateEmergency ? [new { phoneID = 3, name = "Emergency", phoneNumber = "5552223333", primary = false }] : []).ToArray(),
                emails = Array.Empty<object>(), addresses = Array.Empty<object>()
            }),
            "/CaseSyncAPI/case/1/udf" => request.RequestUri.Query.Contains("currentPage=0") ? Handler.Json(new[]
            {
                new { udfid = 1, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true },
                new { udfid = 2, caseID = 1, screenName = "Incident", udfShortName = "Type", udfValue = "Automobile Accident", isActive = true }
            }) : Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Unexpected synthetic path")
        }))));
}
