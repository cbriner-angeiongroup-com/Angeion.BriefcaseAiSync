namespace BriefcaseSync.Core;

public sealed class FieldMapping
{
    public string InventoryId { get; set; } = "";
    public string Destination { get; set; } = "";
    public string UdfPath { get; set; } = "";
    public string StandardProperty { get; set; } = "";
    public string SourceField { get; set; } = "";
    public List<string> StandardProperties { get; set; } = [];
    public string JoinWith { get; set; } = "\n";
    // RejectConflict, PreferUdf, PreferStandard. Both source choices must be explicit.
    public string Precedence { get; set; } = "RejectConflict";
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Required { get; set; }
    public bool Enabled { get; set; } = true;
    public string DisabledReason { get; set; } = "";
    public string Label { get; set; } = "";
    public List<FieldMapping> Parts { get; set; } = [];
    public List<SourceCondition> WhenAll { get; set; } = [];

    internal void ValidateExclusion(bool narrativePart)
    {
        if (!Enabled && (!narrativePart || Required || string.IsNullOrWhiteSpace(DisabledReason)
            || string.IsNullOrWhiteSpace(Label) || Parts.Count > 0 || !string.IsNullOrWhiteSpace(Destination)))
            throw new InvalidOperationException("Only an optional scalar narrative part can be disabled, with a label and explicit reason.");
    }
}

public sealed class SourceCondition
{
    public string UdfPath { get; set; } = "";
    public string SourceField { get; set; } = "";
    public string EqualsValue { get; set; } = "";
}
