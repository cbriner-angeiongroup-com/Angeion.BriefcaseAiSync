using BriefcaseSync.Core;
using Cronos;

namespace BriefcaseSync.Infrastructure;

public sealed class RuntimeSettings
{
    public string ScheduleCron { get; set; } = "*/15 * * * *";
    public long EligibleStatusId { get; set; }
    public bool SendingEnabled { get; set; }
    public bool MappingApproved { get; set; }
    public bool RetainersEnabled { get; set; }
    public long RetainerCategoryId { get; set; }
    public long RetainerSubcategoryId { get; set; }
    public int RunTimeoutMinutes { get; set; } = 50;
    public string PrimaryPhoneType { get; set; } = "Cell";
    public string SecondaryPhoneType { get; set; } = "Phone 2";
    public string EmergencyPhoneType { get; set; } = "Emergency";
    public MappingOptions Mapping { get; set; } = new();
    public List<DocumentTypeMapping> DocumentTypeMappings { get; set; } = DocumentTypes.ExactMatches();

    public static RuntimeSettings From(AppOptions o) => new()
    {
        ScheduleCron = o.ScheduleCron, EligibleStatusId = o.SmartAdvocate.EligibleStatusId, SendingEnabled = o.SendingEnabled,
        MappingApproved = o.MappingApproved, RetainersEnabled = o.SmartAdvocate.RetainersEnabled,
        RetainerCategoryId = o.SmartAdvocate.RetainerCategoryId, RetainerSubcategoryId = o.SmartAdvocate.RetainerSubcategoryId,
        RunTimeoutMinutes = o.RunTimeoutMinutes, PrimaryPhoneType = o.SmartAdvocate.PrimaryPhoneType,
        SecondaryPhoneType = o.SmartAdvocate.SecondaryPhoneType, EmergencyPhoneType = o.SmartAdvocate.EmergencyPhoneType,
        Mapping = System.Text.Json.JsonSerializer.Deserialize<MappingOptions>(System.Text.Json.JsonSerializer.Serialize(o.Mapping))!,
        DocumentTypeMappings = System.Text.Json.JsonSerializer.Deserialize<List<DocumentTypeMapping>>(System.Text.Json.JsonSerializer.Serialize(o.DocumentTypeMappings))!
    };
    public void Apply(AppOptions o)
    {
        o.ScheduleCron = ScheduleCron; o.SmartAdvocate.EligibleStatusId = EligibleStatusId; o.SendingEnabled = SendingEnabled;
        o.MappingApproved = MappingApproved; o.SmartAdvocate.RetainersEnabled = RetainersEnabled;
        o.SmartAdvocate.RetainerCategoryId = RetainerCategoryId; o.SmartAdvocate.RetainerSubcategoryId = RetainerSubcategoryId;
        o.RunTimeoutMinutes = RunTimeoutMinutes; o.SmartAdvocate.PrimaryPhoneType = PrimaryPhoneType;
        o.SmartAdvocate.SecondaryPhoneType = SecondaryPhoneType; o.SmartAdvocate.EmergencyPhoneType = EmergencyPhoneType;
        o.Mapping = Mapping;
        o.DocumentTypeMappings = System.Text.Json.JsonSerializer.Deserialize<List<DocumentTypeMapping>>(System.Text.Json.JsonSerializer.Serialize(DocumentTypeMappings))!;
    }
    public void Validate()
    {
        DocumentTypes.Validate(DocumentTypeMappings);
        if (CronExpression.Parse(ScheduleCron, CronFormat.Standard).GetNextOccurrence(DateTime.UtcNow) == null)
            throw new InvalidOperationException("The schedule has no future occurrence.");
        if (EligibleStatusId < 0 || RunTimeoutMinutes is < 1 or > 50) throw new InvalidOperationException("Use a nonnegative status ID and a 1–50 minute timeout.");
        if (new[] { PrimaryPhoneType, SecondaryPhoneType, EmergencyPhoneType }.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Phone type labels are required.");
        if (RetainersEnabled && (RetainerCategoryId <= 0 || RetainerSubcategoryId <= 0)) throw new InvalidOperationException("Choose the OnTask category ID and Submitted File subcategory ID.");
        if (SendingEnabled && (!MappingApproved || EligibleStatusId <= 0)) throw new InvalidOperationException("Sending needs an approved mapping and a positive eligibility status ID.");
        if (MappingApproved || SendingEnabled) Mapping.Validate();
        if (Mapping.Mappings.Count == 0 || Mapping.Mappings.Count > 100) throw new InvalidOperationException("Supply 1–100 destination mappings.");
    }
}

public sealed record SettingsSnapshot(RuntimeSettings Settings, int Version, DateTimeOffset SavedAtUtc);
public sealed record RunSummary(Guid Id, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, string Outcome, bool DryRun, int Discovered, int Processed, int Failed, int Attention, int ConfigurationVersion);
public sealed record DeliverySummary(long SourceCaseId, string Status, DateTimeOffset UpdatedAtUtc, int DocumentCount, int DeliveredCount, IReadOnlyList<string> ErrorCodes);
public sealed record OperationsDashboard(IReadOnlyList<RunSummary> Runs, IReadOnlyList<DeliverySummary> Deliveries, int TotalCases, int CreatedCases, int AttentionCases, int PendingCases);

public interface IOperationsRepository
{
    Task<SettingsSnapshot> LoadSettings(AppOptions fallback, CancellationToken ct);
    Task<SettingsSnapshot> SaveSettings(RuntimeSettings settings, int expectedVersion, string actor, CancellationToken ct);
    Task<OperationsDashboard> Dashboard(CancellationToken ct);
    Task<SyncState?> FindState(long caseId, CancellationToken ct);
    Task Audit(string actor, string action, long? caseId, CancellationToken ct);
}
