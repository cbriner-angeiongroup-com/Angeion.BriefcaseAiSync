namespace BriefcaseSync.Core;

public sealed class SyncState
{
    public long SourceCaseId { get; set; }
    public string? LeadVendorId { get; set; }
    public string? IntakeId { get; set; }
    public string Status { get; set; } = "Discovered";
    public string? MappingVersion { get; set; }
    public string? Fingerprint { get; set; }
    public IntakeRequest? SubmissionRequest { get; set; }
    public IntakeRequest? PendingUpdateRequest { get; set; }
    public List<FieldError> Errors { get; set; } = [];
    public Dictionary<string, DocumentState> Documents { get; set; } = [];
    public List<ReconciliationRecord> Reconciliations { get; set; } = [];
}

public sealed record ReconciliationRecord(DateTimeOffset AtUtc, string AssetIdentity, string UploadId, string OperatorReference);

public sealed class DocumentState
{
    public string Identity { get; set; } = "";
    public long SourceDocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string? SourceDocumentType { get; set; }
    public UploadInitializationRequest? InitializationRequest { get; set; }
    public string? UploadId { get; set; }
    public string? UploadUrl { get; set; }
    public string UploadMethod { get; set; } = "PUT";
    public Dictionary<string, string> UploadHeaders { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
}
