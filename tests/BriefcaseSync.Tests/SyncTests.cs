using System.Security.Cryptography;
using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class SyncTests
{
    [Fact]
    public async Task SavesFrozenIntentBeforeSendingAndReplaysAfterUncertainResponse()
    {
        var store = new MemoryStore();
        var destination = new FakeDestination();
        destination.OnCreate = request =>
        {
            Assert.Equal("Submitting", store.States[1].Status);
            Assert.Equal(request.LeadVendorId, store.States[1].SubmissionRequest!.LeadVendorId);
            throw new HttpRequestException("synthetic response lost");
        };
        var delivery = new IntakeDelivery(Fixtures.Mapping(), store, destination);
        var state = new SyncState { SourceCaseId = 1 };
        await Assert.ThrowsAsync<HttpRequestException>(() => delivery.Process(state, Fixtures.Snapshot(), true, default));
        var changed = Fixtures.Snapshot(); changed.Case.PrimaryContactNameFirst = "Changed";
        destination.OnCreate = _ => "intake-recovered";
        var recovered = await store.Read(1, default);
        await delivery.Process(recovered, changed, true, default);
        Assert.Equal("Synthetic", destination.Requests[1].ClientFirstName);
        Assert.Equal("Changed", store.States[1].PendingUpdateRequest!.ClientFirstName);
        Assert.Equal("UpdateRequired", store.States[1].Status);
        Assert.Equal("intake-recovered", store.States[1].IntakeId);
    }

    [Fact]
    public async Task ChangedTigerIdCannotCreateAnotherLeadOrSendRetainers()
    {
        var store = new MemoryStore(); var destination = new FakeDestination(); var retainers = new FakeRetainers();
        store.States[1] = new() { SourceCaseId = 1, LeadVendorId = "TIGER-OLD", IntakeId = "existing" };
        var runner = new SyncRunner(new FakeSource(), store, new(Fixtures.Mapping(), store, destination), retainers);
        var result = await runner.Run(true, default);
        Assert.Equal(1, result.Attention); Assert.Empty(destination.Requests); Assert.Equal(0, retainers.Calls);
        Assert.Equal("IdentityConflict", store.States[1].Status); Assert.Equal("TIGER-OLD", store.States[1].LeadVendorId);
    }

    [Fact]
    public async Task CreatedLeadChangesAreRetainedWithoutPostingAnUpdate()
    {
        var store = new MemoryStore(); var destination = new FakeDestination();
        var delivery = new IntakeDelivery(Fixtures.Mapping(), store, destination);
        var state = new SyncState { SourceCaseId = 1 };
        await delivery.Process(state, Fixtures.Snapshot(), true, default);
        var changed = Fixtures.Snapshot(); changed.Case.PrimaryContactNameLast = "Revised";
        await delivery.Process(state, changed, true, default);
        Assert.Single(destination.Requests); Assert.Equal("Revised", state.PendingUpdateRequest!.ClientLastName);
    }

    [Fact]
    public async Task DefinitiveValidationRejectionAllowsCorrectedPayload()
    {
        var store = new MemoryStore(); var destination = new FakeDestination { OnCreate = _ => throw new DestinationRejectedException() };
        var delivery = new IntakeDelivery(Fixtures.Mapping(), store, destination);
        var state = new SyncState { SourceCaseId = 1 };
        await Assert.ThrowsAsync<DestinationRejectedException>(() => delivery.Process(state, Fixtures.Snapshot(), true, default));
        Assert.Null(store.States[1].SubmissionRequest);
        var corrected = Fixtures.Snapshot(); corrected.Case.PrimaryContactNameFirst = "Corrected";
        destination.OnCreate = _ => "created";
        await delivery.Process(state, corrected, true, default);
        Assert.Equal("Corrected", destination.Requests[1].ClientFirstName);
    }

    [Fact]
    public async Task DryRunMakesNoVendorWritesOrStateChanges()
    {
        var store = new MemoryStore(); var destination = new FakeDestination(); var retainers = new FakeRetainers();
        var result = await new SyncRunner(new FakeSource(), store, new(Fixtures.Mapping(), store, destination), retainers).Run(false, default);
        Assert.Equal(1, result.Processed); Assert.Equal(0, store.Saves); Assert.Empty(store.States); Assert.Empty(destination.Requests); Assert.Equal(0, retainers.Calls);
    }

    [Fact]
    public async Task FailedDiscoveryDoesNotDispatchOrSavePartialWork()
    {
        var store = new MemoryStore(); var destination = new FakeDestination();
        var runner = new SyncRunner(new FakeSource { DiscoveryFails = true }, store, new(Fixtures.Mapping(), store, destination), new FakeRetainers());
        await Assert.ThrowsAsync<IOException>(() => runner.Run(true, default));
        Assert.Empty(store.States); Assert.Empty(destination.Requests);
    }

    [Fact]
    public async Task OverlappingRunDoesNoWork()
    {
        var store = new MemoryStore { Busy = true }; var destination = new FakeDestination();
        var result = await new SyncRunner(new FakeSource { DiscoveryFails = true }, store, new(Fixtures.Mapping(), store, destination), new FakeRetainers()).Run(true, default);
        Assert.True(result.OverlapSkipped); Assert.Empty(destination.Requests);
    }

    [Fact]
    public async Task CaseLeavingReadyStatusBeforeLoadIsNotCreated()
    {
        var store = new MemoryStore(); var destination = new FakeDestination();
        var source = new FakeSource { Snapshot = Fixtures.Snapshot() with { Eligible = false } };
        await new SyncRunner(source, store, new(Fixtures.Mapping(), store, destination), new FakeRetainers()).Run(true, default);
        Assert.Empty(destination.Requests);
    }

    [Fact]
    public async Task BoundCaseStillCheckedForRetainersAfterLeavingDiscoveryStatus()
    {
        var store = new MemoryStore(); var destination = new FakeDestination(); var retainers = new FakeRetainers();
        await new IntakeDelivery(Fixtures.Mapping(), store, destination).Process(new() { SourceCaseId = 1 }, Fixtures.Snapshot(), true, default);
        await new SyncRunner(new FakeSource { Ids = [], Snapshot = Fixtures.Snapshot() with { Eligible = false } }, store, new(Fixtures.Mapping(), store, destination), retainers).Run(true, default);
        Assert.Equal(1, retainers.Calls); Assert.Single(destination.Requests);
    }

    [Fact]
    public void MissingOrAmbiguousTigerIdIsRejected()
    {
        var snapshot = Fixtures.Snapshot(); snapshot.Case.CustomFields.RemoveAt(0);
        Assert.Throws<IntakeValidationException>(() => new IntakeMapper(Fixtures.Mapping()).Map(snapshot.Case));
        snapshot = Fixtures.Snapshot(); snapshot.Case.CustomFields.Add(snapshot.Case.CustomFields[0]);
        Assert.Throws<IntakeValidationException>(() => new IntakeMapper(Fixtures.Mapping()).Map(snapshot.Case));
    }

    [Fact]
    public void FullWorkbookProfileRetainsConditionalNarrativesAndFailsForUnknownChoices()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config/mapping.json")));
        var options = document.RootElement.GetProperty("Mapping").Deserialize<MappingOptions>()!;
        Assert.Equal(38, options.Mappings.Count);
        Assert.Contains(options.Mappings, m => m.Parts.Count > 0 && m.Parts.Any(p => p.WhenAll.Count > 0));
        options.Validate(); // The unresolved helmet condition is retained only on an explicitly disabled optional part.
        var mapping = Fixtures.Mapping(); mapping.Mappings.Single(m => m.Destination == "caseType").Values = new() { ["Automobile Accident"] = "Automobile Accident" };
        var snapshot = Fixtures.Snapshot(); snapshot.Case.CustomFields[1].FieldValue = "Other";
        Assert.Throws<IntakeValidationException>(() => new IntakeMapper(mapping).Map(snapshot.Case));
    }

    [Fact]
    public void MobileAloneAndOversizedNarrativesAreRejected()
    {
        var request = new IntakeMapper(Fixtures.Mapping()).Map(Fixtures.Snapshot().Case);
        request.ClientEmail = null; request.ClientMobilePhone = "5551234567"; request.Notes = new string('x', 32769);
        var errors = new IntakeValidator(Fixtures.Mapping()).Validate(request);
        Assert.Contains(errors, e => e.Field == "clientEmail/clientPhone"); Assert.Contains(errors, e => e.Code == "VALUE_TOO_LONG");
    }

    [Fact]
    public void StateCipherRejectsTamperingAndCrossCaseOrCrossScopeReplay()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var cipher = new StateCipher(key, "synthetic-scope");
        var bytes = cipher.Encrypt(new() { SourceCaseId = 1, LeadVendorId = "TIGER-TEST" });
        Assert.Equal("TIGER-TEST", cipher.Decrypt(1, bytes).LeadVendorId);
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(2, bytes));
        Assert.ThrowsAny<CryptographicException>(() => new StateCipher(key, "other").Decrypt(1, bytes));
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(1, bytes));
    }
}
