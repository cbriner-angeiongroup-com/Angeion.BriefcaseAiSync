using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BriefcaseSync.Core;

public sealed class IntakeDelivery(MappingOptions options, ISyncStore store, IBriefcaseDestination destination)
{
    private readonly IntakeMapper mapper = new(options);

    public async Task Process(SyncState state, SourceSnapshot snapshot, bool sendingEnabled, CancellationToken ct)
    {
        var leadId = mapper.LeadVendorId(snapshot.Case);
        if (state.LeadVendorId != null && !string.Equals(state.LeadVendorId, leadId, StringComparison.Ordinal))
        {
            state.Status = "IdentityConflict";
            state.Errors = [new("leadVendorId", "IDENTITY_CHANGED", "Tiger Lead ID changed after binding; reconcile before delivery.")];
            if (sendingEnabled) await store.Save(state, ct);
            return;
        }
        var current = mapper.Map(snapshot.Case, leadId);
        if (options.DescriptionEnabled) current.Description = snapshot.Description;
        var errors = new IntakeValidator(options).Validate(current);
        if (errors.Count > 0) throw new IntakeValidationException(errors);
        var fingerprint = Hash(JsonSerializer.Serialize(current, IntakeRequest.JsonOptions));
        if (!sendingEnabled) return;

        state.LeadVendorId = leadId;
        if (state.IntakeId != null)
        {
            state.PendingUpdateRequest = state.Fingerprint == fingerprint ? null : current;
            state.Status = state.PendingUpdateRequest == null ? "Created" : "UpdateRequired";
            state.Errors = state.PendingUpdateRequest == null ? [] : [new("api", "UPDATE_ENDPOINT_REQUIRED", "Updated answers are retained; the vendor contract has no update endpoint.")];
            await store.Save(state, ct);
            return;
        }
        // A frozen, previously dispatched create is retried only with its original Tiger Lead ID and payload.
        if (state.SubmissionRequest == null)
        {
            if (!snapshot.Eligible) return;
            state.SubmissionRequest = current;
            state.MappingVersion = options.MappingVersion;
            state.Fingerprint = fingerprint;
        }
        state.Status = "Submitting";
        state.Errors.Clear();
        await store.Save(state, ct); // Also atomically enforces unique Tiger ID ownership before HTTP.
        try { state.IntakeId = await destination.Create(state.SubmissionRequest, ct); }
        catch (DestinationRejectedException)
        {
            state.SubmissionRequest = null;
            state.Fingerprint = null;
            state.Status = "Rejected";
            await store.Save(state, ct);
            throw;
        }
        state.SubmissionRequest = null;
        state.Status = "Created";
        // A recovered request may be older than the current source snapshot.
        state.PendingUpdateRequest = state.Fingerprint == fingerprint ? null : current;
        if (state.PendingUpdateRequest != null) state.Status = "UpdateRequired";
        await store.Save(state, ct);
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
