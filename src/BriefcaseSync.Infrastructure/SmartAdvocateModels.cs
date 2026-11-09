using System.Text.Json.Serialization;

namespace BriefcaseSync.Infrastructure;

public sealed class ContactCard
{
    public long ContactId { get; set; }
    public long? GenderId { get; set; }
    public string? Gender { get; set; }
    public string? MaritalStatus { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? Suffix { get; set; }
    public string? MaidenName { get; set; }
    public string? Spouse { get; set; }
    public string? PrimaryLanguage { get; set; }
    public string? SecondaryLanguage { get; set; }
    public string? LicenseNo { get; set; }
    public string? SsnNo { get; set; }
    public string? ContactSubCtg { get; set; }
    public long? ContactSubCtgId { get; set; }
    public long? MaritalStatusId { get; set; }
    public DateTimeOffset? DateOfBirth { get; set; }
    public DateTimeOffset? DateOfDeath { get; set; }
    public bool? Active { get; set; }
    public List<ContactPhone>? Phones { get; set; }
    public List<ContactEmail>? Emails { get; set; }
    public List<ContactAddress>? Addresses { get; set; }
}

public sealed class ContactPhone
{
    public long PhoneId { get; set; }
    public string? Name { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Comments { get; set; }
    public bool? Primary { get; set; }
    public bool? UseForTexting { get; set; }
}

public sealed class ContactEmail
{
    public string? Name { get; set; }
    public bool? Primary { get; set; }
}

public sealed class ContactAddress
{
    public long AddressId { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Country { get; set; }
    public bool? Primary { get; set; }
}

internal sealed class UdfAnswer
{
    [JsonPropertyName("udfid")] public long Id { get; set; }
    public long CaseId { get; set; }
    public string? CaseNumber { get; set; }
    [JsonPropertyName("screenName")] public string? FormName { get; set; }
    [JsonPropertyName("udfShortName")] public string? KeyName { get; set; }
    [JsonPropertyName("udfName")] public string? Name { get; set; }
    [JsonPropertyName("udfValue")] public string? Value { get; set; }
    [JsonPropertyName("isActive")] public bool? IsEnabled { get; set; }
    [JsonPropertyName("type")] public string? ControlTypeName { get; set; }
    [JsonPropertyName("newValues")] public string? SelectOptions { get; set; }
    [JsonPropertyName("requiredField")] public bool IsRequired { get; set; }
    [JsonPropertyName("createdDate")] public DateTimeOffset? CreateDateTime { get; set; }
    [JsonPropertyName("modifiedDate")] public DateTimeOffset? ModifiedDateTime { get; set; }
}
