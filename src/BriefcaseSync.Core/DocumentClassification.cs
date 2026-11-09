namespace BriefcaseSync.Core;

public static class DocumentClassification
{
    public const string Category = "OnTask";
    public const string Subcategory = "Submitted File";

    public static bool Matches(string? category, string? subcategory) =>
        string.Equals(category?.Trim(), Category, StringComparison.OrdinalIgnoreCase)
        && string.Equals(subcategory?.Trim(), Subcategory, StringComparison.OrdinalIgnoreCase);
}
