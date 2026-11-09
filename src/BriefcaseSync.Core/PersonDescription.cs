using System.Globalization;

namespace BriefcaseSync.Core;

public static class PersonDescription
{
    public static string Build(IEnumerable<PersonDescriptionEntry> people)
    {
        var blocks = new List<string>();
        foreach (var group in people.GroupBy(p => p.ContactId).OrderBy(g => g.Key))
        {
            var roles = group.Select(p => p.Role).Distinct(StringComparer.Ordinal).ToArray();
            if (roles.Any(role => role is not ("Client" or "Guardian" or "Representative" or "Other")))
                throw Error("SOURCE_PERSON_ROLE_BINDING_REQUIRED", "Every related-person role needs a verified Client, Guardian, Representative or Other binding.");
            if (roles.Length != 1)
                throw Error("AMBIGUOUS_PERSON_ROLE", "One related person has conflicting role bindings; review their role before sending.");
            var person = group.First();
            if (group.Key <= 0 || string.IsNullOrWhiteSpace(person.FirstName) || string.IsNullOrWhiteSpace(person.LastName))
                throw Error("MISSING_PERSON_DETAILS", "Every included person needs an identity, first name and last name.");
            var lines = new List<string> { "Role: " + roles[0], "First Name: " + SingleLine(person.FirstName), "Last Name: " + SingleLine(person.LastName) };
            if (person.Deceased is { } deceased) lines.Add("Deceased: " + (deceased ? "Yes" : "No"));
            if (person.DateOfBirth is { Year: > 1 } birth) lines.Add("Date of Birth: " + birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(person.Email)) lines.Add("Email: " + SingleLine(person.Email));
            if (!string.IsNullOrWhiteSpace(person.Phone)) lines.Add("Phone: " + SingleLine(person.Phone));
            blocks.Add(string.Join('\n', lines));
        }
        if (blocks.Count == 0) throw Error("MISSING_PERSON_DETAILS", "Person details must include the main contact.");
        return string.Join("\n\n", blocks);
    }

    private static string SingleLine(string value)
    {
        if (value.Any(char.IsControl)) throw Error("INVALID_PERSON_DETAILS", "Person facts must be single-line values.");
        return value.Trim();
    }

    private static IntakeValidationException Error(string code, string message) => new([new("description", code, message)]);
}
