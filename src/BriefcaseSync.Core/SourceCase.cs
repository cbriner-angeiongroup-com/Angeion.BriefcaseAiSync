namespace BriefcaseSync.Core;

public sealed class SourceCase
{
    public long Id { get; set; }
    public List<CustomField> CustomFields { get; set; } = [];
    public Dictionary<string, string?> SourceFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CaseType { get; set; }
    public DateTimeOffset? IncidentDate { get; set; }
    public string? PrimaryContactAddressCity { get; set; }
    public string? PrimaryContactAddressCountryName { get; set; }
    public string? PrimaryContactAddressLine1 { get; set; }
    public string? PrimaryContactAddressLine2 { get; set; }
    public string? PrimaryContactAddressStateCode { get; set; }
    public string? PrimaryContactAddressZipCode { get; set; }
    public DateTimeOffset? PrimaryContactBirthDate { get; set; }
    public string? PrimaryContactEmailAddress { get; set; }
    public string? PrimaryContactGender { get; set; }
    public string? PrimaryContactNameFirst { get; set; }
    public string? PrimaryContactNameLast { get; set; }
    public string? PrimaryContactPhoneNumber { get; set; }
    public string? PrimaryContactSocialSecurityNumber { get; set; }
}

public sealed class CustomField
{
    public string Path { get; set; } = "";
    public string FieldValue { get; set; } = "";
    public bool Enabled { get; set; }
}
