namespace BriefcaseSync.Core;

public sealed class MappingOptions
{
    public string LeadVendorIdUdfPath { get; set; } = "";
    public string MappingVersion { get; set; } = "";
    public bool DescriptionEnabled { get; set; }
    public bool DescriptionContractVerified { get; set; }
    public List<FieldMapping> Mappings { get; set; } = [];
    public Dictionary<string, string[]> CaseSubtypes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Automobile Accident"] = ["General Automobile Accident", "Bicycle Accident", "Bus Accident", "Commercial", "Hit and Run", "Maritime/Boating Accident", "Motorcycle Accident", "Pedestrian Accident", "Rideshare Accident"],
        ["Bair Hugger"] = ["General Bair Hugger"], ["ITC"] = ["General ITC"], ["Wrongful Death"] = ["General Wrongful Death"]
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LeadVendorIdUdfPath) || string.IsNullOrWhiteSpace(MappingVersion) || Mappings.Count == 0)
            throw new InvalidOperationException("Mapping requires the Tiger Lead ID UDF path, version and field mappings.");
        foreach (var mapping in Mappings)
        {
            mapping.ValidateExclusion(narrativePart: false);
            foreach (var part in mapping.Parts) part.ValidateExclusion(narrativePart: true);
        }
        if (new IntakeMapper(this).SourcePaths().Any(p => p.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Replace mapping placeholders with verified SmartAdvocate screen/short-name paths.");
        if (DescriptionEnabled && !DescriptionContractVerified)
            throw new InvalidOperationException("Verify BriefCase description support before enabling additional-person blocks.");
    }
}
