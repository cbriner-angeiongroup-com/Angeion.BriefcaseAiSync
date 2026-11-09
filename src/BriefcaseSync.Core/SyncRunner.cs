namespace BriefcaseSync.Core;

public sealed record SyncResult(int Discovered, int Processed, int Failed, int Attention, bool OverlapSkipped, bool ScheduleSkipped = false);

public sealed class SyncRunner(ISmartAdvocateSource source, ISyncStore store, IntakeDelivery intake, IRetainerDelivery retainers, IRunJournal? journal = null)
{
    public async Task<SyncResult> Run(bool sendingEnabled, CancellationToken ct)
    {
        await using var run = await store.TryAcquireRun(ct);
        if (run == null) return new(0, 0, 0, 0, true);
        Guid? runId = journal == null ? Guid.NewGuid() : await journal.Begin(ct);
        if (runId == null) return new(0, 0, 0, 0, false, true);
        try
        {
        // Complete discovery first. A partial/truncated lookup never initiates new vendor work.
        var discovered = await source.Discover(ct);
        if (sendingEnabled) await store.Discover(discovered, ct);
        var ids = sendingEnabled ? await store.Cases(ct) : discovered;
        int processed = 0, failed = 0, attention = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var state = sendingEnabled ? await store.Read(id, ct) : new SyncState { SourceCaseId = id };
            try
            {
                var snapshot = await source.Load(id, ct);
                if (!snapshot.Eligible && state.LeadVendorId == null) continue;
                await intake.Process(state, snapshot, sendingEnabled, ct);
                if (sendingEnabled && state.IntakeId != null && state.Status != "IdentityConflict")
                    await retainers.Deliver(state, ct);
                if (state.Status is "IdentityConflict" or "UpdateRequired" || state.Documents.Values.Any(d => d.Status == "InitUnresolved")) attention++;
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failed++;
                if (!sendingEnabled) continue;
                if (error is IntakeValidationException identity && identity.Errors.Any(e => e.Code == "IDENTITY_ALREADY_BOUND"))
                {
                    // The failed unique claim never committed. Preserve the previous persisted binding.
                    state = await store.Read(id, ct);
                    state.Status = "IdentityConflict";
                }
                // Only application-owned codes go into persistent outcomes; never exception messages.
                state.Errors = error is IntakeValidationException validation
                    ? validation.Errors.Select(e => new FieldError(e.Field, e.Code, "Source mapping validation failed.")).ToList()
                    : [new("sync", "PROCESSING_FAILED", "Inspect protected state and vendor correlation.")];
                if (state.Status is not ("IdentityConflict" or "UpdateRequired" or "Rejected")) state.Status = "RetryRequired";
                await store.Save(state, ct);
            }
        }
        var result = new SyncResult(discovered.Count, processed, failed, attention, false);
        if (journal != null) await journal.Complete(runId.Value, result, failed > 0 || attention > 0 ? "Attention" : "Completed", ct);
        return result;
        }
        catch
        {
            if (journal != null)
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await journal.Complete(runId.Value, null, ct.IsCancellationRequested ? "Interrupted" : "Failed", finish.Token);
            }
            throw;
        }
    }
}
