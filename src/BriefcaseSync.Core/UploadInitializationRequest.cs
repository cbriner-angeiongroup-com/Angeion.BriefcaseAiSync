using System.Text.Json.Serialization;

namespace BriefcaseSync.Core;

public sealed record UploadInitializationRequest(
    [property: JsonPropertyName("intakeId")] string IntakeId,
    [property: JsonPropertyName("leadVendorId")] string LeadVendorId,
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSize")] long FileSize);
