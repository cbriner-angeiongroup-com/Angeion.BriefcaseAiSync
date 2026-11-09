using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace BriefcaseSync.Core;

public sealed class IntakeValidator(MappingOptions options)
{
    public static readonly HashSet<string> DateFields = new(["clientDateOfBirth", "incidentDate", "statuteOfLimitationsDate"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> YesNo = new(["clientNonUsAddress", "clientSmsOptOut", "incidentDateNotExact", "inLitigation", "sovereignImmunity", "recordsOnly", "injured", "treatment"], StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> Lengths = new()
    {
        ["leadVendorId"]=255, ["clientFirstName"]=255, ["clientLastName"]=255, ["clientMaidenName"]=255, ["clientSpouseName"]=255,
        ["clientDriversLicenseNumber"]=150, ["clientBillingStreet"]=255, ["clientBillingCity"]=40, ["clientBillingState"]=80,
        ["clientBillingPostalCode"]=20, ["clientBillingCountry"]=80, ["clientEmergencyContact"]=255, ["clientGuardianPerson"]=32768,
        ["accidentPoliceReport"]=4000, ["witnessesInformation"]=16000, ["injuryDescription"]=32768, ["ambulanceInformation"]=32768,
        ["currentAccidentTreatment"]=16000, ["plaintiffHealthInsuranceInfo"]=5000, ["vehicleInformation"]=10000,
        ["defendantInformation"]=3000, ["priorMvas"]=5000, ["description"]=32768, ["notes"]=32768
    };
    private static readonly Dictionary<string, string[]> Picks = new()
    {
        ["clientGender"]=["Female", "Male", "Non-Binary - They/Them", "Other", "Undetermined"],
        ["clientMaritalStatus"]=["Married", "Single", "Divorced", "Widow", "Widower", "Other"],
        ["clientLanguagesSpoken"]=["English", "French", "Spanish", "Portuguese", "Creole"]
    };
    private const string StateCodes = "AL AK AZ AR CA CO CT DE DC FL GA HI ID IL IN IA KS KY LA ME MD MA MI MN MS MO MT NE NV NH NJ NM NY NC ND OH OK OR PA RI SC SD TN TX UT VT VA WA WV WI WY";
    private const string StateNames = "Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|District of Columbia|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming";
    private static readonly HashSet<string> States = new(StateCodes.Split(' ').Concat(StateCodes.Split(' ').Zip(StateNames.Split('|'), (code, name) => code + " " + name)).Append("N/A"), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<FieldError> Validate(IntakeRequest request)
    {
        List<FieldError> errors = [];
        void Error(string field, string code, string message) => errors.Add(new(field, code, message));
        foreach (var required in new[] { "leadVendorId", "clientFirstName", "clientLastName", "caseType" })
            if (!request.Fields().TryGetValue(required, out var field) || string.IsNullOrWhiteSpace(field.GetString())) Error(required, "MISSING_REQUIRED_FIELD", "A value is required.");
        if (string.IsNullOrWhiteSpace(request.ClientEmail) && string.IsNullOrWhiteSpace(request.ClientPhone)) Error("clientEmail/clientPhone", "MISSING_REQUIRED_FIELD", "An email or primary phone is required; mobile alone is insufficient.");
        foreach (var (key, element) in request.Fields())
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) if (!Picks[key].Contains(item.GetString(), StringComparer.OrdinalIgnoreCase)) Error(key, "INVALID_PICKLIST_VALUE", "Unsupported multi-select choice.");
                continue;
            }
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (Lengths.TryGetValue(key, out var max) && value.Length > max) Error(key, "VALUE_TOO_LONG", $"Maximum length is {max}.");
            if (DateFields.Contains(key) && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Error(key, "INVALID_DATE", "Use a valid yyyy-MM-dd date.");
            if (YesNo.Contains(key) && !new[] { "Yes", "No" }.Contains(value, StringComparer.OrdinalIgnoreCase)) Error(key, "INVALID_BOOLEAN", "Use Yes or No; leave unanswered values absent.");
            if (Picks.TryGetValue(key, out var choices) && !choices.Contains(value, StringComparer.OrdinalIgnoreCase)) Error(key, "INVALID_PICKLIST_VALUE", "Unsupported picklist value.");
            if (key == "clientEmail" && (!MailAddress.TryCreate(value, out var address) || address.Address != value)) Error(key, "INVALID_EMAIL", "Invalid email address.");
            if (key is "clientPhone" or "clientMobilePhone" or "clientEmergencyPhone")
            {
                var digits = Regex.Replace(value, @"[\s()+.\-]", "");
                if (!Regex.IsMatch(digits, @"^1?\d{10}$")) Error(key, "INVALID_FORMAT", "Use a ten-digit US phone, optionally preceded by 1.");
            }
            if (key == "clientSocialSecurityNumber" && !Regex.IsMatch(value, @"^(\d{9}|\d{3}-\d{2}-\d{4})$")) Error(key, "INVALID_FORMAT", "Use nine digits or XXX-XX-XXXX.");
            if (key == "caseState" && !States.Contains(value)) Error(key, "INVALID_PICKLIST_VALUE", "Use a US state code, its full label (for example FL Florida), or N/A.");
        }
        if (!string.IsNullOrWhiteSpace(request.CaseType))
        {
            var caseType = options.CaseSubtypes.SingleOrDefault(p => string.Equals(p.Key, request.CaseType, StringComparison.OrdinalIgnoreCase));
            if (caseType.Value is null) Error("caseType", "INVALID_CASE_TYPE", "Case type is outside configured accepted reference values.");
            else if (!string.IsNullOrWhiteSpace(request.CaseSubType) && !caseType.Value.Contains(request.CaseSubType, StringComparer.OrdinalIgnoreCase)) Error("caseSubType", "INVALID_CASE_SUB_TYPE", "Subtype does not belong to the selected case type.");
        }
        return errors;
    }
}
