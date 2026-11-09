using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;

namespace BriefcaseSync.Web.Services;

public sealed class DemoOperationsRepository(AppOptions defaults) : IOperationsRepository
{
    private readonly object gate = new();
    private SettingsSnapshot current = new(DemoData.Settings(defaults), 1, DateTimeOffset.UtcNow.AddHours(-2));
    public Task<SettingsSnapshot> LoadSettings(AppOptions fallback, CancellationToken ct)
    { lock (gate) return Task.FromResult(Clone(current)); }
    public Task<SettingsSnapshot> SaveSettings(RuntimeSettings settings, int expectedVersion, string actor, CancellationToken ct)
    {
        settings.Validate();
        lock (gate)
        {
            if (expectedVersion != current.Version) throw new UiOperationException("Settings changed since you opened them. Reload before saving.");
            current = Clone(new(settings, current.Version + 1, DateTimeOffset.UtcNow)); return Task.FromResult(Clone(current));
        }
    }
    public Task<OperationsDashboard> Dashboard(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var runs = Enumerable.Range(0, 8).Select(i => new RunSummary(new Guid(i + 1, 0, 0, new byte[8]), now.AddMinutes(-8 - 15 * i), now.AddMinutes(-6 - 15 * i), i == 2 ? "Attention" : "Completed", true, 6, 6, i == 2 ? 1 : 0, i == 2 ? 1 : 0, 1)).ToArray();
        var rows = new[] { new DeliverySummary(10427, "Created", now.AddMinutes(-6), 1, 1, []), new(10426, "Created", now.AddMinutes(-6), 2, 2, []),
            new(10425, "UpdateRequired", now.AddMinutes(-8), 1, 1, ["UPDATE_ENDPOINT_REQUIRED"]), new(10424, "RetryRequired", now.AddMinutes(-22), 1, 0, ["PROCESSING_FAILED"]), new(10423, "Created", now.AddMinutes(-23), 1, 1, []), new(10422, "Discovered", now.AddMinutes(-24), 0, 0, []) };
        return Task.FromResult(new OperationsDashboard(runs, rows, 6, 3, 2, 1));
    }
    public Task<SyncState?> FindState(long caseId, CancellationToken ct) => Task.FromResult<SyncState?>(null);
    public Task Audit(string actor, string action, long? caseId, CancellationToken ct) => Task.CompletedTask;
    private static SettingsSnapshot Clone(SettingsSnapshot value) => JsonSerializer.Deserialize<SettingsSnapshot>(JsonSerializer.Serialize(value))!;
}

public static class DemoData
{
    public static RuntimeSettings Settings(AppOptions defaults)
    {
        var result = RuntimeSettings.From(defaults);
        result.EligibleStatusId = 321; result.RetainersEnabled = true; result.RetainerCategoryId = 75; result.RetainerSubcategoryId = 614;
        result.Mapping = JsonSerializer.Deserialize<MappingOptions>(JsonSerializer.Serialize(defaults.Mapping).Replace("REPLACE_WITH_VERIFIED_SCREEN", "Intake"))!;
        result.Mapping.LeadVendorIdUdfPath = "Identity/Tiger"; result.Mapping.MappingVersion = "farrar-ball-demo-v1"; return result;
    }
    public static SourceSnapshot Snapshot(MappingOptions options) => new(new SourceCase
    {
        Id = 10428, CaseType = "MVA-FB-SE", PrimaryContactNameFirst = "Alex", PrimaryContactNameLast = "Morgan", PrimaryContactEmailAddress = "alex.morgan@example.invalid",
        PrimaryContactPhoneNumber = "(555) 234-0186", PrimaryContactAddressLine1 = "123 Example Avenue", PrimaryContactAddressCity = "Austin",
        PrimaryContactAddressStateCode = "TX", PrimaryContactAddressZipCode = "78701", PrimaryContactAddressCountryName = "United States",
        PrimaryContactBirthDate = new DateTimeOffset(1990, 4, 18, 0, 0, 0, TimeSpan.Zero), IncidentDate = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
        SourceFields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PrimaryContactMiddleName"] = null,
            ["PrimaryContactMobilePhone"] = null, ["PrimaryContactEmergencyPhone"] = null, ["PrimaryContactEmergencyContact"] = null,
            ["PrimaryContactSmsOptOut"] = "No", ["PrimaryContactMaritalStatus"] = null, ["PrimaryContactMaidenName"] = null,
            ["PrimaryContactSpouseName"] = null, ["PrimaryContactLanguages"] = "[\"English\"]", ["PrimaryContactDriversLicenseNumber"] = null,
            ["PrimaryContactSubcategory"] = null, ["PrimaryContactDeceased"] = null
        },
        CustomFields = [new() { Path = options.LeadVendorIdUdfPath, FieldValue = "TIGER-DEMO-10428", Enabled = true },
            new() { Path = "Intake/FB-U030", FieldValue = "Automobile Accident", Enabled = true }, new() { Path = "Intake/FB-U031", FieldValue = "TX", Enabled = true },
            new() { Path = "Intake/FB-U033", FieldValue = "Exact", Enabled = true }, new() { Path = "Intake/FB-U034", FieldValue = "Synthetic example: rear-end collision at a traffic light. Client reports neck and shoulder pain.", Enabled = true },
            new() { Path = "Intake/FB-U052", FieldValue = "Yes", Enabled = true }, new() { Path = "Intake/FB-U053", FieldValue = "Neck and shoulder pain", Enabled = true }]
    }, true, null);
}
