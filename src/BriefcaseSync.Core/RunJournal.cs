namespace BriefcaseSync.Core;

public interface IRunJournal
{
    Task<Guid?> Begin(CancellationToken ct);
    Task Complete(Guid runId, SyncResult? result, string outcome, CancellationToken ct);
}
