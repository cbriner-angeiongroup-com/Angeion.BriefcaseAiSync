using System.Net.Http.Json;
using System.Text.Json;
using BriefcaseSync.Core;

namespace BriefcaseSync.Infrastructure;

public sealed class SmartAdvocateSource(SmartAdvocateClient api, SmartAdvocateOptions options, MappingOptions mapping) : ISmartAdvocateSource
{
    public async Task<long> FindCaseNumber(string caseNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseNumber) || caseNumber.Length > 100 || caseNumber.Any(char.IsControl))
            throw new ArgumentException("Enter a valid exact case number.");
        // The documented CaseInfo endpoint is an exact case-number lookup; no wildcard search or first match fallback.
        var matches = await api.Read<List<CaseHeader>>("case/CaseInfo?Casenumber=" + Uri.EscapeDataString(caseNumber.Trim()) + "&AddContactInfo=false&AddReferrals=false", ct);
        if (matches.Count != 1 || matches[0].CaseId <= 0 || !string.Equals(matches[0].CaseNumber, caseNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Case lookup did not return an exact, unique case.");
        return matches[0].CaseId;
    }
    public async Task<IReadOnlyList<long>> Discover(CancellationToken ct)
    {
        options.TestTrigger.Validate();
        // The documented lookup returns an array, not a paged result. Top is a case-state filter, NOT a row limit.
        using var response = await api.Get($"Search/caselookup?SearchType=CASESTATUS&Criteria={options.EligibleStatusId}&Top={options.CaseStateFilter}", ct);
        var cases = await response.Content.ReadFromJsonAsync<List<CaseLookup>>(SmartAdvocateClient.JsonOptions, ct)
            ?? throw new InvalidDataException("Missing status lookup.");
        if (cases.Any(c => c.CaseId <= 0)) throw new InvalidDataException("Status lookup omitted case identities.");
        if (response.Headers.TryGetValues("X-Total-Count", out var counts) && (!long.TryParse(counts.Single(), out var total) || total != cases.Count))
            throw new InvalidDataException("Status lookup was truncated.");
        // Add only the explicitly configured test case; never scan the alternate status in bulk.
        var testCases = options.TestTrigger.Enabled ? new[] { options.TestTrigger.SourceCaseId } : [];
        return cases.Select(c => c.CaseId).Concat(testCases).Distinct().Order().ToArray();
    }

    public async Task<SourceSnapshot> Load(long caseId, CancellationToken ct) => (await LoadSnapshot(caseId, false, ct)).Snapshot;

    public Task<SourcePreviewSnapshot> LoadPreview(long caseId, CancellationToken ct) => LoadSnapshot(caseId, true, ct);

    private async Task<SourcePreviewSnapshot> LoadSnapshot(long caseId, bool preview, CancellationToken ct)
    {
        options.TestTrigger.Validate();
        var header = await api.Read<CaseHeader>($"case/{caseId}", ct);
        if (header.CaseId != caseId) throw new InvalidDataException("Source returned the wrong case.");
        // Only the current main status can trigger a new intake; secondary statuses never qualify.
        var eligible = CaseEligibility.Matches(caseId, header.CaseStatusId, options.EligibleStatusId, options.TestTrigger);
        // Recheck the current main status instead of trusting the earlier search result.
        var people = await api.Read<List<Plaintiff>>($"case/{caseId}/plaintiffs?TableType=All&AddContactInfo=true", ct);
        if (people.Any(p => p.Id <= 0 || p.ContactId <= 0) || people.Select(p => p.Id).Distinct().Count() != people.Count)
            throw new InvalidDataException("Invalid related-person identities.");
        var related = people.Where(p => !p.MarkedAsRemoved).ToArray();
        var primaryContacts = related.Where(p => p.PrimaryContact).Take(2).ToArray();
        var errors = new List<FieldError>();
        string? ResolveChoice(string? label, long? id, IReadOnlyDictionary<long, string> translations, string field)
        {
            try { return ContactFieldRules.Choice(label, id, translations, field); }
            catch (IntakeValidationException error) when (preview) { errors.AddRange(error.Errors); return null; }
        }
        if (primaryContacts.Length != 1)
        {
            if (!preview) throw new PrimaryContactSelectionException(primaryContacts.Length > 1);
            errors.Add(primaryContacts.Length == 0
                ? new("primaryContact", "MISSING_PRIMARY_CONTACT", "No primary contact is selected in SmartAdvocate. This case cannot sync until exactly one is selected. Contact fields remain empty.")
                : new("primaryContact", "AMBIGUOUS_PRIMARY_CONTACT", "Multiple primary contacts are selected in SmartAdvocate. This case cannot sync until exactly one is selected. Contact fields remain empty."));
        }
        var cards = new Dictionary<long, ContactCard>();
        foreach (var person in primaryContacts.Length == 1 || mapping.DescriptionEnabled ? related : [])
        {
            if (cards.ContainsKey(person.ContactId)) continue;
            var card = await api.Read<ContactCard>($"contact/fullInfo/{person.ContactId}", ct);
            if (card.ContactId != person.ContactId || card.Phones == null || card.Emails == null || card.Addresses == null)
                throw new InvalidDataException("Invalid or incomplete contact card.");
            cards.Add(card.ContactId, card);
        }
        var source = new SourceCase { Id = caseId, IncidentDate = Date(header.Incident?.IncidentDate), SourceFields = ContactFields() };
        if (primaryContacts.Length == 1)
        {
            var contact = cards[primaryContacts[0].ContactId];
            if (contact.Active == false) throw new InvalidDataException("Primary contact is inactive.");
            if (contact.Phones!.Any(p => p.Primary == null) || contact.Emails!.Any(p => p.Primary == null) || contact.Addresses!.Any(p => p.Primary == null))
                throw new InvalidDataException("Missing explicit primary flags.");
            One(contact.Phones!.Where(p => p.Primary == true));
            var phone = One(contact.Phones!.Where(p => p.Primary == true && Same(p.Name, options.PrimaryPhoneType)));
            var secondary = One(contact.Phones!.Where(p => Same(p.Name, options.SecondaryPhoneType)));
            var emergency = One(contact.Phones!.Where(p => ContactFieldRules.IsEmergencyPhone(p.Name, options.EmergencyPhoneType, options.EmergencyPhoneLabelPrefix)));
            var email = One(contact.Emails!.Where(p => p.Primary == true));
            var address = One(contact.Addresses!.Where(p => p.Primary == true));
            var gender = ResolveChoice(contact.Gender, contact.GenderId, options.GenderValues, "clientGender");
            var maritalStatus = ResolveChoice(contact.MaritalStatus, contact.MaritalStatusId, options.MaritalStatusValues, "clientMaritalStatus");
            source = new SourceCase
            {
                Id = caseId,
                PrimaryContactNameFirst = contact.FirstName, PrimaryContactNameLast = contact.LastName,
                PrimaryContactEmailAddress = email?.Name, PrimaryContactPhoneNumber = phone?.PhoneNumber,
                PrimaryContactBirthDate = Date(contact.DateOfBirth),
                PrimaryContactGender = gender,
                PrimaryContactSocialSecurityNumber = contact.SsnNo,
                PrimaryContactAddressLine1 = address?.Address1,
                PrimaryContactAddressLine2 = address?.Address2,
                PrimaryContactAddressCity = address?.City, PrimaryContactAddressStateCode = address?.State,
                PrimaryContactAddressZipCode = address?.Zip, PrimaryContactAddressCountryName = address?.Country,
                IncidentDate = Date(header.Incident?.IncidentDate),
                SourceFields = ContactFields(contact, phone, secondary, emergency, maritalStatus)
            };
        }
        source.CaseType = header.CaseType;
        try { await LoadUdfs(caseId, source, ct); }
        catch (InvalidDataException) when (preview)
        {
            source.CustomFields.Clear();
            errors.Add(new("customFields", "SOURCE_UDF_UNAVAILABLE", "SmartAdvocate UDF answers could not be loaded completely. UDF-based fields are unavailable and this case cannot sync."));
        }
        string? description = null;
        if (mapping.DescriptionEnabled)
        {
            try { description = Describe(related, cards); }
            catch (IntakeValidationException error) when (preview) { errors.AddRange(error.Errors); }
        }
        return new(new(source, eligible, description), errors);
    }

    private Dictionary<string, string?> ContactFields(ContactCard? contact = null, ContactPhone? phone = null,
        ContactPhone? secondary = null, ContactPhone? emergency = null, string? maritalStatus = null) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrimaryContactMobilePhone"] = secondary?.PhoneNumber,
        ["PrimaryContactEmergencyPhone"] = emergency?.PhoneNumber,
        ["PrimaryContactEmergencyContact"] = ContactFieldRules.EmergencyContact(emergency?.Comments, emergency?.Name, options.EmergencyPhoneLabelPrefix),
        ["PrimaryContactMiddleName"] = contact?.MiddleName,
        ["PrimaryContactSmsOptOut"] = phone?.UseForTexting switch { true => "No", false => "Yes", null => null },
        ["PrimaryContactMaritalStatus"] = maritalStatus,
        ["PrimaryContactMaidenName"] = contact?.MaidenName,
        ["PrimaryContactSpouseName"] = contact?.Spouse,
        ["PrimaryContactLanguages"] = contact == null ? null : Languages(contact),
        ["PrimaryContactDriversLicenseNumber"] = contact?.LicenseNo,
        ["PrimaryContactSubcategory"] = contact?.ContactSubCtg,
        ["PrimaryContactDeceased"] = Date(contact?.DateOfDeath) != null ? "Yes" : null
    };

    private async Task LoadUdfs(long caseId, SourceCase source, CancellationToken ct)
    {
        var answers = await api.Pages<UdfAnswer>($"case/{caseId}/udf", f => f.Id, ct, include: f => f.CaseId != 0);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in answers)
        {
            // Definition-only rows are returned alongside case answers. Only owning case answers can be mapped.
            if (field.CaseId == 0) continue;
            if (field.CaseId != caseId) throw new InvalidDataException("UDF answer belongs to a different case.");
            if (string.IsNullOrWhiteSpace(field.KeyName) || field.IsEnabled == null)
                throw new InvalidDataException("UDF answer lacks short-name or active metadata.");
            var path = string.IsNullOrEmpty(field.FormName) ? field.KeyName : field.FormName + "/" + field.KeyName;
            if (!paths.Add(path)) throw new InvalidDataException("Ambiguous UDF answer path.");
            source.CustomFields.Add(new() { Path = path, Enabled = field.IsEnabled.Value, FieldValue = field.Value ?? "" });
        }
    }

    private string Describe(Plaintiff[] people, Dictionary<long, ContactCard> cards)
    {
        var entries = new List<PersonDescriptionEntry>();
        foreach (var person in people)
        {
            var c = cards[person.ContactId];
            try
            {
                var email = One(c.Emails!.Where(e => e.Primary == true));
                var phone = One(c.Phones!.Where(p => p.Primary == true));
                entries.Add(new(c.ContactId, options.PersonRoles.GetValueOrDefault(person.RoleId), c.FirstName, c.LastName,
                    Date(c.DateOfBirth), Date(c.DateOfDeath) != null ? true : null, email?.Name, phone?.PhoneNumber));
            }
            catch (InvalidDataException)
            {
                throw new IntakeValidationException([new("description", "AMBIGUOUS_PERSON_CONTACT", "A related person has multiple primary phone or email selections.")]);
            }
        }
        // Minor and family relationship remain absent until explicit source bindings are verified.
        return PersonDescription.Build(entries);
    }

    public async Task<IReadOnlyList<SourceDocument>> Retainers(long caseId, CancellationToken ct)
    {
        if (!options.RetainersEnabled) return [];
        var docs = await api.Pages<CaseDocument>($"case/{caseId}/documents?categoryIDs={options.RetainerCategoryId}&subcategoryIDs={options.RetainerSubcategoryId}", d => d.DocumentId, ct);
        if (docs.Any(d => d.CaseId != caseId)) throw new InvalidDataException("Document belongs to another case.");
        return docs.Where(d => !d.DeleteFlag && d.CategoryId == options.RetainerCategoryId && d.SubCategoryId == options.RetainerSubcategoryId
                && DocumentClassification.Matches(d.CategoryName, d.SubCategoryName))
            .Select(d => new SourceDocument(d.DocumentId, d.DocumentName ?? "", d.DocType)).ToArray();
    }

    public async Task<Stream> Download(long documentId, CancellationToken ct)
    {
        using var response = await api.Get($"case/document/{documentId}/content", ct);
        if (response.Content.Headers.ContentLength > RetainerDelivery.MaximumFileSize) throw new InvalidDataException("Retainer exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var output = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > RetainerDelivery.MaximumFileSize) throw new InvalidDataException("Retainer exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
    private static T? One<T>(IEnumerable<T> values, bool required = false) where T : class
    {
        var result = values.Take(2).ToArray();
        if (result.Length > 1 || required && result.Length == 0) throw new InvalidDataException("Missing or ambiguous source selection.");
        return result.SingleOrDefault();
    }
    private static DateTimeOffset? Date(DateTimeOffset? value) => value is { Year: > 1 } ? value : null;
    private static string? Languages(ContactCard c)
    {
        var values = new[] { c.PrimaryLanguage, c.SecondaryLanguage }.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? null : JsonSerializer.Serialize(values);
    }
}

public sealed class PrimaryContactSelectionException(bool ambiguous) : Exception("Source primary contact selection requires correction.")
{
    public bool Ambiguous { get; } = ambiguous;
}

public sealed class CaseLookup { public long CaseId { get; set; } }
public sealed class CaseHeader
{
    public long CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? CaseType { get; set; }
    public long CaseStatusId { get; set; }
    public Incident? Incident { get; set; }
    public List<SecondaryStatus> SecondStatuses { get; set; } = [];
}
public sealed class SecondaryStatus { public long StatusId { get; set; } }
public sealed class Incident { public DateTimeOffset? IncidentDate { get; set; } }
public sealed class Plaintiff
{
    public long Id { get; set; }
    public long ContactId { get; set; }
    public long RoleId { get; set; }
    public bool PrimaryContact { get; set; }
    public bool MarkedAsRemoved { get; set; }
}
public sealed class CaseDocument
{
    public long DocumentId { get; set; }
    public long CaseId { get; set; }
    public string? DocumentName { get; set; }
    public string? DocType { get; set; }
    public long CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public long SubCategoryId { get; set; }
    public string? SubCategoryName { get; set; }
    public bool DeleteFlag { get; set; }
}
