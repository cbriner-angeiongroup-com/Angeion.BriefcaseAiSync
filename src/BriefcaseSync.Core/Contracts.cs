namespace BriefcaseSync.Core;

public sealed record SourceSnapshot(SourceCase Case, bool Eligible, string? Description);
public sealed record SourcePreviewSnapshot(SourceSnapshot Snapshot, IReadOnlyList<FieldError> Errors);
public sealed record SourceDocument(long Id, string FileName, string? DocType = null);

public interface ISmartAdvocateSource
{
    Task<IReadOnlyList<long>> Discover(CancellationToken ct);
    Task<SourceSnapshot> Load(long caseId, CancellationToken ct);
    Task<IReadOnlyList<SourceDocument>> Retainers(long caseId, CancellationToken ct);
    Task<Stream> Download(long documentId, CancellationToken ct);
}

public interface ISyncStore
{
    Task<IAsyncDisposable?> TryAcquireRun(CancellationToken ct);
    Task Discover(IReadOnlyList<long> caseIds, CancellationToken ct);
    Task<IReadOnlyList<long>> Cases(CancellationToken ct);
    Task<SyncState> Read(long caseId, CancellationToken ct);
    Task Save(SyncState state, CancellationToken ct);
}

public interface IRetainerAssets
{
    Task Stage(string identity, Stream bytes, CancellationToken ct);
    Task<Stream> Open(string identity, CancellationToken ct);
}

public interface IBriefcaseDestination
{
    Task<string> Create(IntakeRequest request, CancellationToken ct);
}

public interface IRetainerDelivery
{
    Task Deliver(SyncState state, CancellationToken ct);
}
