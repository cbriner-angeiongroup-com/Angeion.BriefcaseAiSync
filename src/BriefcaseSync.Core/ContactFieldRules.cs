namespace BriefcaseSync.Core;

public static class ContactFieldRules
{
    public static string? Choice(string? label, long? id, IReadOnlyDictionary<long, string> translations, string field)
    {
        if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        if (id == null) return null;
        if (translations.TryGetValue(id.Value, out var value)) return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        throw new IntakeValidationException([new(field, "SOURCE_CHOICE_BINDING_REQUIRED", "The source returned a numeric choice without a verified tenant translation.")]);
    }

    public static bool IsEmergencyPhone(string? label, string exactType, string prefix) =>
        string.Equals(label?.Trim(), exactType.Trim(), StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(prefix) && label?.Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    public static string? EmergencyContact(string? comments, string? label, string prefix)
    {
        if (!string.IsNullOrWhiteSpace(comments)) return comments.Trim();
        if (string.IsNullOrWhiteSpace(prefix) || label?.Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase) != true) return null;
        var name = label.Trim()[prefix.Trim().Length..].Trim();
        return name.Length == 0 ? null : name;
    }
}
