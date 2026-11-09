namespace BriefcaseSync.Core;

public sealed class DocumentTypeMapping
{
    public string SourceType { get; set; } = "";
    public string DocumentType { get; set; } = "";
}

public static class DocumentTypes
{
    public static IReadOnlyList<string> Accepted { get; } = Array.AsReadOnly(new[]
    {
        "Signed Retainer Agreement", "Unsigned Retainer Agreement", "ID Document",
        "Police Report", "Medical Record", "Other"
    });

    // Explicit initial mappings, never a fallback for a missing source value.
    public static List<DocumentTypeMapping> ExactMatches() => Accepted.Select(type =>
        new DocumentTypeMapping { SourceType = type, DocumentType = type }).ToList();

    public static void Validate(IReadOnlyList<DocumentTypeMapping>? mappings)
    {
        if (mappings == null || mappings.Count is < 1 or > 100
            || mappings.Any(m => m == null || string.IsNullOrWhiteSpace(m.SourceType)
                || m.SourceType.Length > 255 || m.SourceType != m.SourceType.Trim() || m.SourceType.Any(char.IsControl)
                || !Accepted.Contains(m.DocumentType, StringComparer.Ordinal))
            || mappings.Select(m => m.SourceType).Distinct(StringComparer.Ordinal).Count() != mappings.Count)
            throw new InvalidOperationException("Supply unique source document types and supported BriefCase document types.");
    }

    public static string Resolve(string? sourceType, IReadOnlyList<DocumentTypeMapping> mappings)
    {
        Validate(mappings);
        var match = mappings.SingleOrDefault(m => string.Equals(m.SourceType, sourceType, StringComparison.Ordinal));
        return match?.DocumentType ?? throw new InvalidOperationException("Document type mapping is required before upload.");
    }

    public static UploadInitializationRequest RequireFrozenRequest(DocumentState session)
    {
        var request = session.InitializationRequest;
        if (request == null || !Accepted.Contains(request.DocumentType, StringComparer.Ordinal))
            throw new InvalidOperationException("Upload has no valid frozen document type; operator review is required before initialization.");
        return request;
    }
}
