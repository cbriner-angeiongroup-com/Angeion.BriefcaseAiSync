using System.Text.Json;

namespace BriefcaseSync.Core;

public sealed record PreviewField(string Key, string Source, string? Value, string Outcome, IReadOnlyList<FieldError> Errors);
public sealed record PreviewDocument(long SourceDocumentId, string FileName, string Action, string? SourceType = null, string? DocumentType = null);
public sealed record SyncPreview(long SourceCaseId, string CaseNumber, bool Eligible, string Action, string Explanation,
    IntakeRequest? ProposedIntake, IReadOnlyList<PreviewField> Fields, IReadOnlyList<PreviewDocument> Documents,
    IReadOnlyList<FieldError> Errors, string MappingVersion, DateTimeOffset GeneratedAtUtc);

public static class PreviewPlanner
{
    public static SyncPreview Build(string caseNumber, SourceSnapshot snapshot, SyncState? state, MappingOptions options, IReadOnlyList<SourceDocument> documents,
        IReadOnlyList<FieldError>? configurationErrors = null, IReadOnlyList<DocumentTypeMapping>? documentTypeMappings = null)
    {
        var mapper = new IntakeMapper(options);
        var errors = new List<FieldError>(configurationErrors ?? []);
        string leadId = "";
        try { leadId = mapper.LeadVendorId(snapshot.Case); }
        catch (IntakeValidationException e) { errors.AddRange(e.Errors); }
        IntakeRequest candidate;
        try { candidate = mapper.Map(snapshot.Case, leadId); }
        catch (IntakeValidationException e) { candidate = e.PartialRequest ?? new(); errors.AddRange(e.Errors); }
        if (options.DescriptionEnabled) candidate.Description = snapshot.Description;
        errors.AddRange(new IntakeValidator(options).Validate(candidate));
        errors = errors.DistinctBy(e => (e.Field, e.Code)).ToList();
        var missingSources = errors.Where(e => e.Code == "MISSING_SOURCE").Select(e => e.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        errors.RemoveAll(e => e.Code == "MISSING_REQUIRED_FIELD" && missingSources.Contains(e.Field));
        var action = "Create intake";
        var explanation = "A new intake would be created using the Tiger Lead ID.";
        var actual = candidate;
        if (state?.LeadVendorId != null && state.LeadVendorId != leadId)
        { action = "Identity conflict"; explanation = "The Tiger Lead ID differs from the existing binding. Delivery requires reconciliation."; }
        else if (errors.Count > 0)
        {
            action = "Validation blocked";
            explanation = errors.Any(e => e.Code is "MISSING_PRIMARY_CONTACT" or "AMBIGUOUS_PRIMARY_CONTACT")
                ? "This case cannot sync until exactly one primary contact is selected in SmartAdvocate. Available case data is shown below."
                : "Correct the fields below before this case can be delivered.";
        }
        else if (state?.IntakeId != null)
        {
            var same = state.Fingerprint == IntakeDelivery.Hash(JsonSerializer.Serialize(candidate, IntakeRequest.JsonOptions));
            action = same ? "Already synced" : "Update required";
            explanation = same ? "The intake matches the last acknowledged payload. Eligible documents are still checked."
                : "The intake has changed. The current vendor API does not support updates; the changed answers would be retained for review.";
        }
        else if (state?.SubmissionRequest != null)
        { action = "Retry frozen intake"; explanation = "Recovery would resend the previously frozen request, shown below, using the original Tiger Lead ID."; actual = state.SubmissionRequest; }
        else if (!snapshot.Eligible)
        { action = "Not eligible"; explanation = "The case does not currently have the configured sync status. No new intake would be sent."; }

        var values = actual.Fields();
        var fields = new List<PreviewField>();
        foreach (var key in options.Mappings.Select(m => m.Destination).Prepend("leadVendorId").Concat(values.Keys)
            .Concat(options.DescriptionEnabled ? ["description"] : []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mapping = options.Mappings.FirstOrDefault(m => m.Destination.Equals(key, StringComparison.OrdinalIgnoreCase));
            var fieldErrors = errors.Where(e => e.Field.Equals(key, StringComparison.OrdinalIgnoreCase) || e.Field.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var value = values.TryGetValue(key, out var element) && element.ValueKind != JsonValueKind.Null
                ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString() : null;
            fields.Add(new(key, action == "Retry frozen intake" ? "Frozen request · original binding" : key == "leadVendorId" ? options.LeadVendorIdUdfPath : mapping == null ? "Related people" : Describe(mapping), value,
                fieldErrors.Length > 0 ? "Blocked" : string.IsNullOrWhiteSpace(value) ? "Omitted" : "Included", fieldErrors));
            if (action != "Retry frozen intake" && mapping != null)
                foreach (var part in mapping.Parts.Where(p => !p.Enabled))
                    fields.Add(new(key + "/" + (string.IsNullOrWhiteSpace(part.InventoryId) ? part.Label : part.InventoryId),
                        Describe(part) + " · " + part.DisabledReason, null, "Blocked", []));
        }
        var docPlan = new List<PreviewDocument>();
        foreach (var document in documents)
        {
            string? documentType = null;
            try { documentType = DocumentTypes.Resolve(document.DocType, documentTypeMappings ?? DocumentTypes.ExactMatches()); }
            catch (InvalidOperationException) { }
            docPlan.Add(new(document.Id, document.FileName, documentType == null ? "Document type mapping required"
                : state?.IntakeId == null ? "After intake creation" : "Compare content and upload if changed", document.DocType, documentType));
        }
        if (state != null)
            foreach (var d in state.Documents.Values.Where(d => d.Status != "Delivered"))
            {
                docPlan.RemoveAll(p => p.SourceDocumentId == d.SourceDocumentId);
                docPlan.Add(new(d.SourceDocumentId, d.FileName, d.Status is "Initializing" or "InitUnresolved" ? "Reconciliation required"
                    : d.InitializationRequest == null && d.UploadId == null ? "Frozen document type review required" : "Resume frozen upload",
                    d.SourceDocumentType, d.InitializationRequest?.DocumentType));
            }
        if (action is "Identity conflict" or "Validation blocked" or "Not eligible")
            docPlan = docPlan.Select(d => d with { Action = "Blocked with intake" }).ToList();
        return new(snapshot.Case.Id, caseNumber, snapshot.Eligible, action, explanation, actual, fields, docPlan, errors,
            action == "Retry frozen intake" ? state?.MappingVersion ?? options.MappingVersion : options.MappingVersion, DateTimeOffset.UtcNow);
    }
    private static string Describe(FieldMapping m) => m.Parts.Count > 0 ? $"{m.Parts.Count(p => p.Enabled)} labeled answers" : !string.IsNullOrWhiteSpace(m.UdfPath) ? m.UdfPath
        : !string.IsNullOrWhiteSpace(m.SourceField) ? m.SourceField : m.StandardProperties.Count > 0 ? string.Join(" + ", m.StandardProperties) : m.StandardProperty;
}
