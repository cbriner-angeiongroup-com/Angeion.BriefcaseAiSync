using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class RetainerDelivery(ISmartAdvocateSource source, ISyncStore store, IRetainerAssets assets, BriefCaseAiApiClient api, bool enabled,
    IReadOnlyList<DocumentTypeMapping> documentTypeMappings) : IRetainerDelivery
{
    public const long MaximumFileSize = 104857600;

    public async Task Deliver(SyncState state, CancellationToken ct)
    {
        if (!enabled) return;
        if (state.IntakeId == null || state.LeadVendorId == null || state.Status == "IdentityConflict")
            throw new InvalidOperationException("Retainer requires a reconciled intake identity.");
        // Finish frozen work first, even if the source document was changed/deleted or its case left the ready status.
        foreach (var session in state.Documents.Values.Where(d => d.Status != "Delivered").ToArray())
            await Send(state, session, ct);
        foreach (var document in await source.Retainers(state.SourceCaseId, ct))
        {
            var documentType = DocumentTypes.Resolve(document.DocType, documentTypeMappings);
            ValidateFileName(document.FileName);
            await using var stream = await source.Download(document.Id, ct);
            if (!stream.CanSeek || stream.Length is < 1 or > MaximumFileSize) throw new InvalidDataException("Invalid retainer content size.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            var identity = IntakeDelivery.Hash(JsonSerializer.Serialize(new object[] { state.SourceCaseId, document.Id, hash }));
            if (state.Documents.ContainsKey(identity)) continue;
            stream.Position = 0;
            await assets.Stage(identity, stream, ct); // Immutable bytes exist before the durable intent references them.
            var session = new DocumentState
            {
                Identity = identity, SourceDocumentId = document.Id, SourceDocumentType = document.DocType,
                FileName = new(document.FileName.Select(c => "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray())
            };
            session.InitializationRequest = new(state.IntakeId, state.LeadVendorId, documentType, session.FileName, stream.Length);
            state.Documents.Add(identity, session);
            await store.Save(state, ct);
            await Send(state, session, ct);
        }
    }

    private async Task Send(SyncState state, DocumentState session, CancellationToken ct)
    {
        if (session.Status is "Initializing" or "InitUnresolved")
        {
            session.Status = "InitUnresolved";
            await store.Save(state, ct);
            throw new InvalidOperationException("Upload initialization is uncertain and requires operator reconciliation.");
        }
        if (session.UploadId != null)
        {
            var (status, body) = await api.Post("retainer/complete", new { uploadId = session.UploadId }, ct);
            if (status == HttpStatusCode.OK) { await Confirm(state, session, status, body, ct); return; }
            if (status != HttpStatusCode.Conflict || body.ErrorCode != "UPLOAD_NOT_COMPLETED") throw new FirmApiException(status, body);
            if (session.ExpiresAt == null) throw new InvalidDataException("Upload session expiry is absent.");
            if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                DocumentTypes.RequireFrozenRequest(session); // Legacy sessions cannot acquire a guessed type on replacement.
                session.UploadId = null; session.UploadUrl = null; session.UploadHeaders.Clear(); session.Status = "Pending";
                await store.Save(state, ct);
            }
        }
        await using var stream = await assets.Open(session.Identity, ct);
        if (!stream.CanSeek || stream.Length is < 1 or > MaximumFileSize) throw new InvalidDataException("Invalid staged retainer.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        var identity = IntakeDelivery.Hash(JsonSerializer.Serialize(new object[] { state.SourceCaseId, session.SourceDocumentId, hash }));
        if (identity != session.Identity) throw new InvalidDataException("Staged retainer fingerprint changed.");
        stream.Position = 0;
        if (session.UploadId == null)
        {
            var request = DocumentTypes.RequireFrozenRequest(session);
            if (request.IntakeId != state.IntakeId || request.LeadVendorId != state.LeadVendorId
                || request.FileName != session.FileName || request.FileSize != stream.Length)
                throw new InvalidDataException("Frozen upload inputs do not match staged delivery.");
            session.Status = "Initializing";
            await store.Save(state, ct); // Crash after this point cannot cause an automatic fresh init.
            try
            {
                var (status, body) = await api.Post("retainer/init", request, ct);
                if (status != HttpStatusCode.OK)
                {
                    bool unavailable = status == HttpStatusCode.ServiceUnavailable && body.ErrorCode == "UPLOAD_UNAVAILABLE" && body.Status == "Error";
                    session.Status = unavailable || (int)status is >= 400 and < 500 ? "Rejected" : "InitUnresolved";
                    await store.Save(state, ct);
                    throw new FirmApiException(status, body);
                }
                if (!string.Equals(body.Status, "Success", StringComparison.OrdinalIgnoreCase) || body.IntakeId != state.IntakeId
                    || string.IsNullOrWhiteSpace(body.UploadId) || body.UploadMethod != "PUT"
                    || !Uri.TryCreate(body.UploadUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo)
                    || body.ExpiresAt == null || body.ExpiresAt <= DateTimeOffset.UtcNow || body.MaxFileSizeBytes == null || body.MaxFileSizeBytes < stream.Length)
                    throw new InvalidDataException("Invalid retainer upload session.");
                session.UploadId = body.UploadId; session.UploadUrl = body.UploadUrl; session.UploadMethod = body.UploadMethod;
                session.UploadHeaders = body.UploadHeaders ?? []; session.ExpiresAt = body.ExpiresAt; session.Status = "Initialized";
                await store.Save(state, ct);
            }
            catch
            {
                if (session.Status == "Initializing")
                {
                    session.Status = "InitUnresolved";
                    // Cancellation/crash still leaves the committed Initializing marker for the next run.
                    if (!ct.IsCancellationRequested) await store.Save(state, ct);
                }
                throw;
            }
        }
        session.Status = "Uploading";
        await store.Save(state, ct);
        await api.Put(session, stream, ContentType(session.FileName), ct);
        session.Status = "Uploaded";
        await store.Save(state, ct);
        var (completeStatus, complete) = await api.Post("retainer/complete", new { uploadId = session.UploadId }, ct);
        await Confirm(state, session, completeStatus, complete, ct);
    }

    private async Task Confirm(SyncState state, DocumentState session, HttpStatusCode status, ApiResponse body, CancellationToken ct)
    {
        BriefCaseAiApiClient.RequireSuccess(status, body);
        if (body.IntakeId != state.IntakeId || body.UploadId != session.UploadId) throw new InvalidDataException("Completion identity mismatch.");
        session.Status = "Delivered"; session.UploadUrl = null; session.UploadHeaders.Clear();
        await store.Save(state, ct);
    }
    public static void ValidateFileName(string name)
    {
        var extension = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(extension) || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Retainer requires a filename with a supported extension.");
    }
    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", _ => "application/octet-stream"
    };
}
