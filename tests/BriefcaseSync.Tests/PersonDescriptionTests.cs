using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class PersonDescriptionTests
{
    [Fact]
    public async Task SharedProfileIncludesTheSingleAdultPrimaryContactInDescription()
    {
        var profile = Profile();
        Assert.True(profile.Mapping.DescriptionEnabled);
        Assert.True(profile.Mapping.DescriptionContractVerified);
        Assert.Equal("Client", profile.SmartAdvocate.PersonRoles[26411]);
        var options = Options(profile.SmartAdvocate.PersonRoles);
        using var client = Client(options, additional: false);
        var snapshot = await new SmartAdvocateSource(client, options, Mapping()).Load(1, default);
        Assert.Equal("Role: Client\nFirst Name: Synthetic Main\nLast Name: Person\nDate of Birth: 1980-02-03\nEmail: main@example.invalid\nPhone: 5551234567", snapshot.Description);
        var destination = new FakeDestination();
        await new IntakeDelivery(Mapping(), new MemoryStore(), destination).Process(new() { SourceCaseId = 1 }, snapshot, true, default);
        Assert.Equal(snapshot.Description, Assert.Single(destination.Requests).Description);
        Assert.Null(destination.Requests[0].ClientGuardianPerson);
    }

    [Fact]
    public async Task MainGuardianAndInjuredClientEachHaveTheirOwnBlockAndContactFacts()
    {
        var roles = new Dictionary<long, string> { [26411] = "Client", [90001] = "Guardian" };
        var options = Options(roles);
        using var client = Client(options, additional: true);
        var snapshot = await new SmartAdvocateSource(client, options, Mapping()).Load(1, default);
        var blocks = snapshot.Description!.Split("\n\n");
        Assert.Equal(2, blocks.Length);
        Assert.StartsWith("Role: Guardian\nFirst Name: Synthetic Main\n", blocks[0]);
        Assert.Contains("Email: main@example.invalid", blocks[0]);
        Assert.Contains("Phone: 5551234567", blocks[0]);
        Assert.StartsWith("Role: Client\nFirst Name: Synthetic Injured\n", blocks[1]);
        Assert.Contains("Email: injured@example.invalid", blocks[1]);
        Assert.Contains("Phone: 5552345678", blocks[1]);
        Assert.Contains("Date of Birth: 2014-07-08", blocks[1]);
        Assert.DoesNotContain("main@example.invalid", blocks[1]);
        Assert.DoesNotContain("Minor:", snapshot.Description);
        Assert.DoesNotContain("Relationship:", snapshot.Description);
        Assert.DoesNotContain("Deceased:", snapshot.Description);
        Assert.Equal("Synthetic Main", snapshot.Case.PrimaryContactNameFirst);
        var preview = PreviewPlanner.Build("SYNTHETIC-1", snapshot, null, Mapping(), []);
        Assert.Equal(snapshot.Description, preview.ProposedIntake!.Description);
        Assert.Equal("Included", Assert.Single(preview.Fields, f => f.Key == "description").Outcome);
    }

    [Fact]
    public async Task UnverifiedRolesBlockSendingAndShowAFieldIssueWithoutLosingTheMainContact()
    {
        var options = Options(new() { [90001] = "Guardian" });
        using var client = Client(options, additional: true);
        var source = new SmartAdvocateSource(client, options, Mapping());
        var strict = await Assert.ThrowsAsync<IntakeValidationException>(() => source.Load(1, default));
        Assert.Contains(strict.Errors, e => e.Code == "SOURCE_PERSON_ROLE_BINDING_REQUIRED");
        var loaded = await source.LoadPreview(1, default);
        Assert.Null(loaded.Snapshot.Description);
        Assert.Equal("Synthetic Main", loaded.Snapshot.Case.PrimaryContactNameFirst);
        var preview = PreviewPlanner.Build("SYNTHETIC-1", loaded.Snapshot, null, Mapping(), [], loaded.Errors);
        Assert.Equal("Validation blocked", preview.Action);
        var field = Assert.Single(preview.Fields, f => f.Key == "description");
        Assert.Equal("Blocked", field.Outcome);
        Assert.Contains(field.Errors, e => e.Code == "SOURCE_PERSON_ROLE_BINDING_REQUIRED");
    }

    [Fact]
    public void DuplicateRolesAreCollapsedButConflictingRolesAndMissingFactsRequireReview()
    {
        var person = new PersonDescriptionEntry(100, "Client", "Synthetic", "Person");
        Assert.Equal(PersonDescription.Build([person]), PersonDescription.Build([person, person]));
        Assert.Equal("AMBIGUOUS_PERSON_ROLE", Assert.Single(Assert.Throws<IntakeValidationException>(() => PersonDescription.Build([person, person with { Role = "Guardian" }])).Errors).Code);
        Assert.Equal("MISSING_PERSON_DETAILS", Assert.Single(Assert.Throws<IntakeValidationException>(() => PersonDescription.Build([person with { LastName = " " }])).Errors).Code);
        Assert.Equal("INVALID_PERSON_DETAILS", Assert.Single(Assert.Throws<IntakeValidationException>(() => PersonDescription.Build([person with { FirstName = "Synthetic\nRole: Guardian" }])).Errors).Code);
        Assert.Equal("MISSING_PERSON_DETAILS", Assert.Single(Assert.Throws<IntakeValidationException>(() => PersonDescription.Build([])).Errors).Code);
    }

    [Fact]
    public async Task ExistingIntakesRetainThePersonCorrectionWithoutAnotherCreateAndFrozenRetriesStayFrozen()
    {
        var mapping = Mapping();
        var snapshot = Fixtures.Snapshot() with { Description = PersonDescription.Build([new(100, "Client", "Synthetic", "Person")]) };
        var prior = new IntakeMapper(mapping).Map(snapshot.Case);
        var destination = new FakeDestination();
        var store = new MemoryStore();
        var state = new SyncState { SourceCaseId = 1, LeadVendorId = prior.LeadVendorId, IntakeId = "synthetic-existing", Fingerprint = IntakeDelivery.Hash(JsonSerializer.Serialize(prior, IntakeRequest.JsonOptions)) };
        await new IntakeDelivery(mapping, store, destination).Process(state, snapshot, true, default);
        Assert.Empty(destination.Requests);
        Assert.Equal("UpdateRequired", state.Status);
        Assert.Equal(snapshot.Description, state.PendingUpdateRequest!.Description);

        state = new() { SourceCaseId = 1, LeadVendorId = prior.LeadVendorId, SubmissionRequest = prior, MappingVersion = "synthetic-old", Fingerprint = IntakeDelivery.Hash(JsonSerializer.Serialize(prior, IntakeRequest.JsonOptions)) };
        await new IntakeDelivery(mapping, store, destination).Process(state, snapshot, true, default);
        Assert.Null(Assert.Single(destination.Requests).Description);
        Assert.Equal("synthetic-old", state.MappingVersion);
        Assert.Equal(snapshot.Description, state.PendingUpdateRequest!.Description);
    }

    [Fact]
    public void PersonDatesPreserveTheirCalendarDateAndUnknownDeceasedStatusIsOmitted()
    {
        var text = PersonDescription.Build([new(100, "Representative", "Synthetic", "Person", new(1980, 2, 3, 0, 0, 0, TimeSpan.FromHours(14)), true)]);
        Assert.Contains("Date of Birth: 1980-02-03", text);
        Assert.Contains("Deceased: Yes", text);
        Assert.DoesNotContain("\\n", text);
    }

    private static AppOptions Profile() => JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/mapping.json")))!;
    private static MappingOptions Mapping()
    {
        var mapping = Fixtures.Mapping(); mapping.DescriptionEnabled = true; mapping.DescriptionContractVerified = true;
        return mapping;
    }
    private static SmartAdvocateOptions Options(Dictionary<long, string> roles) => new()
    {
        ApiUrl = "https://sa.example.invalid/CaseSyncAPI/", PersonalAccessToken = "synthetic", EligibleStatusId = 321, PersonRoles = roles
    };
    private static SmartAdvocateClient Client(SmartAdvocateOptions options, bool additional) => new(options, new HttpClient(new Handler(request =>
        Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var path when path.Contains("authenticate") => Handler.Json(new { token = "synthetic-token" }),
            "/CaseSyncAPI/case/1" => Handler.Json(new { caseID = 1, caseStatusID = 321 }),
            "/CaseSyncAPI/case/1/plaintiffs" => Handler.Json(new[]
            {
                new { id = 10, contactID = 100, primaryContact = true, roleID = additional ? 90001 : 26411, markedAsRemoved = false },
                new { id = 11, contactID = 101, primaryContact = false, roleID = 26411, markedAsRemoved = !additional },
                new { id = 12, contactID = 102, primaryContact = false, roleID = 90002, markedAsRemoved = true }
            }),
            "/CaseSyncAPI/contact/fullInfo/100" => Handler.Json(Contact(100, "Synthetic Main", "1980-02-03T00:00:00+14:00", "main@example.invalid", "5551234567")),
            "/CaseSyncAPI/contact/fullInfo/101" => Handler.Json(Contact(101, "Synthetic Injured", "2014-07-08T00:00:00Z", "injured@example.invalid", "5552345678")),
            "/CaseSyncAPI/case/1/udf" => request.RequestUri.Query.Contains("currentPage=0") ? Handler.Json(new[]
            {
                new { udfid = 1, caseID = 1, screenName = "Identity", udfShortName = "Tiger", udfValue = "TIGER-SYNTHETIC", isActive = true },
                new { udfid = 2, caseID = 1, screenName = "Incident", udfShortName = "Type", udfValue = "Automobile Accident", isActive = true }
            }) : Handler.Json(Array.Empty<object>()),
            _ => throw new InvalidOperationException("Only active synthetic people may be loaded.")
        }))));
    private static object Contact(long id, string first, string birth, string email, string phone) => new
    {
        contactID = id, firstName = first, lastName = "Person", dateOfBirth = birth, active = true,
        phones = new[] { new { phoneID = id, name = "Cell", phoneNumber = phone, primary = true } },
        emails = new[] { new { name = email, primary = true } }, addresses = Array.Empty<object>()
    };
}
