using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class AppOptions
{
    public bool SendingEnabled { get; set; }
    public bool MappingApproved { get; set; }
    public bool ManagedConfigurationEnabled { get; set; }
    public int ConfigurationVersion { get; set; }
    public DateTimeOffset ConfigurationSavedAtUtc { get; set; }
    public string ScheduleCron { get; set; } = "*/15 * * * *";
    // Stable deployment identity; changing this starts a different sync history.
    public string Scope { get; set; } = "";
    public string SqlConnectionString { get; set; } = "";
    public string StateEncryptionKey { get; set; } = "";
    public string BlobServiceUri { get; set; } = "";
    public string AssetContainer { get; set; } = "retainer-assets";
    public string? ManagedIdentityClientId { get; set; }
    public int RunTimeoutMinutes { get; set; } = 50;
    public SmartAdvocateOptions SmartAdvocate { get; set; } = new();
    public BriefcaseOptions Briefcase { get; set; } = new();
    public MappingOptions Mapping { get; set; } = new();
    public List<DocumentTypeMapping> DocumentTypeMappings { get; set; } = DocumentTypes.ExactMatches();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Scope) || Scope.Length > 100 || string.IsNullOrWhiteSpace(SqlConnectionString))
            throw new InvalidOperationException("Scope and SQL connection string are required.");
        if (Convert.FromBase64String(StateEncryptionKey).Length != 32)
            throw new InvalidOperationException("StateEncryptionKey must encode exactly 32 bytes.");
        if (RunTimeoutMinutes is < 1 or > 1440) throw new InvalidOperationException("Invalid run timeout.");
        SmartAdvocate.Validate();
        Mapping.Validate();
        DocumentTypes.Validate(DocumentTypeMappings);
        if (SendingEnabled && !MappingApproved) throw new InvalidOperationException("Mapping approval is required to enable delivery.");
        if (SendingEnabled)
        {
            RequireHttps(Briefcase.ApiUrl);
            RequireHttps(BlobServiceUri);
            if (string.IsNullOrWhiteSpace(Briefcase.ClientId) || string.IsNullOrWhiteSpace(Briefcase.ClientSecret))
                throw new InvalidOperationException("BriefCase OAuth credentials are required.");
        }
    }

    internal static void RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Service base URL must use HTTPS without credentials, query or fragment.");
    }
}

public sealed class SmartAdvocateOptions
{
    public string ApiUrl { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public long EligibleStatusId { get; set; }
    public TestTriggerOptions TestTrigger { get; set; } = new();
    public int CaseStateFilter { get; set; } = 2;
    public int PageSize { get; set; } = 200;
    public int MaxPages { get; set; } = 10000;
    public string PrimaryPhoneType { get; set; } = "Cell";
    public string SecondaryPhoneType { get; set; } = "Phone 2";
    public string EmergencyPhoneType { get; set; } = "Emergency";
    public string EmergencyPhoneLabelPrefix { get; set; } = "";
    public long RetainerCategoryId { get; set; }
    public long RetainerSubcategoryId { get; set; }
    public bool RetainersEnabled { get; set; }
    // Explicit role/choice bindings; numeric vendor IDs and role labels are never guessed.
    public Dictionary<long, string> PersonRoles { get; set; } = [];
    public Dictionary<long, string> GenderValues { get; set; } = [];
    public Dictionary<long, string> MaritalStatusValues { get; set; } = [];
    public void ValidateConnection()
    {
        AppOptions.RequireHttps(ApiUrl);
        if (!ApiUrl.EndsWith('/')) throw new InvalidOperationException("SmartAdvocate API base URL must include a trailing slash.");
        if (string.IsNullOrWhiteSpace(PersonalAccessToken) && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)))
            throw new InvalidOperationException("Supply SmartAdvocate PAT or username and password.");
    }

    public void Validate()
    {
        ValidatePreview();
        if (EligibleStatusId <= 0)
            throw new InvalidOperationException("Configure a positive eligibility status ID and valid paging limits.");
    }

    public void ValidatePreview()
    {
        ValidateConnection();
        TestTrigger.Validate();
        if (EligibleStatusId < 0 || CaseStateFilter is < 0 or > 2 || PageSize is < 1 or > 1000 || MaxPages is < 1 or > 10000)
            throw new InvalidOperationException("Configure valid source paging limits and a nonnegative status ID.");
        if (RetainersEnabled && (RetainerCategoryId <= 0 || RetainerSubcategoryId <= 0))
            throw new InvalidOperationException("The OnTask category ID and Submitted File subcategory ID are required.");
        if (PersonRoles.Values.Any(v => v is not ("Client" or "Guardian" or "Representative" or "Other")))
            throw new InvalidOperationException("Person role bindings must use workbook role labels.");
    }
}

public sealed class BriefcaseOptions
{
    public string ApiUrl { get; set; } = "https://fbtrial.my.salesforce.com/";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
