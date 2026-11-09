using System.Text.Json;
using System.Text.Json.Serialization;

namespace BriefcaseSync.Core;

public sealed class IntakeRequest
{
    [JsonPropertyName("leadVendorId")]
    public string? LeadVendorId { get; set; }
    [JsonPropertyName("clientFirstName")]
    public string? ClientFirstName { get; set; }
    [JsonPropertyName("clientLastName")]
    public string? ClientLastName { get; set; }
    [JsonPropertyName("clientEmail")]
    public string? ClientEmail { get; set; }
    [JsonPropertyName("clientPhone")]
    public string? ClientPhone { get; set; }
    [JsonPropertyName("clientMobilePhone")]
    public string? ClientMobilePhone { get; set; }
    [JsonPropertyName("clientDateOfBirth")]
    public string? ClientDateOfBirth { get; set; }
    [JsonPropertyName("clientGender")]
    public string? ClientGender { get; set; }
    [JsonPropertyName("clientMaritalStatus")]
    public string? ClientMaritalStatus { get; set; }
    [JsonPropertyName("clientMaidenName")]
    public string? ClientMaidenName { get; set; }
    [JsonPropertyName("clientSpouseName")]
    public string? ClientSpouseName { get; set; }
    [JsonPropertyName("clientLanguagesSpoken")]
    public string[]? ClientLanguagesSpoken { get; set; }
    [JsonPropertyName("clientDriversLicenseNumber")]
    public string? ClientDriversLicenseNumber { get; set; }
    [JsonPropertyName("clientSocialSecurityNumber")]
    public string? ClientSocialSecurityNumber { get; set; }
    [JsonPropertyName("clientBillingStreet")]
    public string? ClientBillingStreet { get; set; }
    [JsonPropertyName("clientBillingCity")]
    public string? ClientBillingCity { get; set; }
    [JsonPropertyName("clientBillingState")]
    public string? ClientBillingState { get; set; }
    [JsonPropertyName("clientBillingPostalCode")]
    public string? ClientBillingPostalCode { get; set; }
    [JsonPropertyName("clientBillingCountry")]
    public string? ClientBillingCountry { get; set; }
    [JsonPropertyName("clientNonUsAddress")]
    public string? ClientNonUsAddress { get; set; }
    [JsonPropertyName("clientEmergencyContact")]
    public string? ClientEmergencyContact { get; set; }
    [JsonPropertyName("clientEmergencyPhone")]
    public string? ClientEmergencyPhone { get; set; }
    [JsonPropertyName("clientGuardianPerson")]
    public string? ClientGuardianPerson { get; set; }
    [JsonPropertyName("clientSmsOptOut")]
    public string? ClientSmsOptOut { get; set; }
    [JsonPropertyName("caseType")]
    public string? CaseType { get; set; }
    [JsonPropertyName("caseSubType")]
    public string? CaseSubType { get; set; }
    [JsonPropertyName("incidentDate")]
    public string? IncidentDate { get; set; }
    [JsonPropertyName("incidentDateNotExact")]
    public string? IncidentDateNotExact { get; set; }
    [JsonPropertyName("statuteOfLimitationsDate")]
    public string? StatuteOfLimitationsDate { get; set; }
    [JsonPropertyName("inLitigation")]
    public string? InLitigation { get; set; }
    [JsonPropertyName("caseState")]
    public string? CaseState { get; set; }
    [JsonPropertyName("sovereignImmunity")]
    public string? SovereignImmunity { get; set; }
    [JsonPropertyName("recordsOnly")]
    public string? RecordsOnly { get; set; }
    [JsonPropertyName("accidentPoliceReport")]
    public string? AccidentPoliceReport { get; set; }
    [JsonPropertyName("witnessesInformation")]
    public string? WitnessesInformation { get; set; }
    [JsonPropertyName("injured")]
    public string? Injured { get; set; }
    [JsonPropertyName("injuryDescription")]
    public string? InjuryDescription { get; set; }
    [JsonPropertyName("ambulanceInformation")]
    public string? AmbulanceInformation { get; set; }
    [JsonPropertyName("treatment")]
    public string? Treatment { get; set; }
    [JsonPropertyName("currentAccidentTreatment")]
    public string? CurrentAccidentTreatment { get; set; }
    [JsonPropertyName("plaintiffHealthInsuranceInfo")]
    public string? PlaintiffHealthInsuranceInfo { get; set; }
    [JsonPropertyName("vehicleInformation")]
    public string? VehicleInformation { get; set; }
    [JsonPropertyName("defendantInformation")]
    public string? DefendantInformation { get; set; }
    [JsonPropertyName("priorMvas")]
    public string? PriorMvas { get; set; }
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    public static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true };
    public Dictionary<string, JsonElement> Fields() => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(this, JsonOptions))!;
}
