using System.Security.Cryptography;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

// Administrative tool, invoked through restricted deployment/job execution access, never a public API.
public sealed class UploadReconciliation(ISyncStore store, IRetainerAssets assets, BriefCaseAiApiClient api)
{
    public async Task Reconcile(UploadReconciliationRequest request, CancellationToken ct)
    {
        if (request.SourceCaseId <= 0 || string.IsNullOrWhiteSpace(request.UploadId) || string.IsNullOrWhiteSpace(request.OperatorReference)
            || !request.FirmVerifiedExactFile) throw new InvalidOperationException("Reconciliation requires firm verification and an operator audit reference.");
        await using var run = await store.TryAcquireRun(ct) ?? throw new InvalidOperationException("Another run is active.");
        var state = await store.Read(request.SourceCaseId, ct);
        if (state.IntakeId == null || state.Status == "IdentityConflict" || !state.Documents.TryGetValue(request.AssetIdentity, out var session)
            || session.Status is not ("Initializing" or "InitUnresolved")) throw new InvalidOperationException("No matching unresolved initialization.");
        await using var bytes = await assets.Open(session.Identity, ct);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(bytes, ct));
        if (!string.Equals(hash, request.VerifiedContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Verified file hash does not match frozen bytes.");
        // Repeated complete is safe. No fresh initialization or upload is issued by this tool.
        var (status, body) = await api.Post("retainer/complete", new { uploadId = request.UploadId }, ct);
        BriefCaseAiApiClient.RequireSuccess(status, body);
        if (body.IntakeId != state.IntakeId || body.UploadId != request.UploadId) throw new InvalidOperationException("Firm confirmation identity mismatch.");
        session.UploadId = request.UploadId; session.Status = "Delivered"; session.UploadUrl = null; session.UploadHeaders.Clear();
        state.Reconciliations.Add(new(DateTimeOffset.UtcNow, session.Identity, request.UploadId, request.OperatorReference));
        await store.Save(state, ct);
    }
}

public sealed class UploadReconciliationRequest
{
    public long SourceCaseId { get; set; }
    public string AssetIdentity { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string VerifiedContentSha256 { get; set; } = "";
    public bool FirmVerifiedExactFile { get; set; }
    public string OperatorReference { get; set; } = "";
}
