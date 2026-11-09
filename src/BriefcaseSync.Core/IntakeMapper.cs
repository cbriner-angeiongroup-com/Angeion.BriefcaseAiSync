using System.Globalization;
using System.Reflection;

namespace BriefcaseSync.Core;

public sealed record FieldError(string Field, string Code, string Message);
public sealed class IntakeValidationException(IReadOnlyList<FieldError> errors, IntakeRequest? partialRequest = null) : Exception("Intake validation failed: " + string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}")))
{
    public IReadOnlyList<FieldError> Errors { get; } = errors;
    public IntakeRequest? PartialRequest { get; } = partialRequest;
}

public sealed class IntakeMapper(MappingOptions options)
{
    public static string? Udf(SourceCase source, string path)
    {
        var matches = source.CustomFields.Where(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new IntakeValidationException([new(path, "AMBIGUOUS_SOURCE", "Multiple UDFs match the configured full path.")]);
        if (matches.Count == 1 && !matches[0].Enabled) throw new IntakeValidationException([new(path, "INACTIVE_SOURCE", "The mapped UDF is inactive.")]);
        return matches.SingleOrDefault()?.FieldValue;
    }

    public string LeadVendorId(SourceCase source)
    {
        if (string.IsNullOrWhiteSpace(options.LeadVendorIdUdfPath))
            throw new IntakeValidationException([new("leadVendorId", "MISSING_SOURCE_BINDING", "Configure the exact Tiger Lead ID UDF path.")]);
        var value = Udf(source, options.LeadVendorIdUdfPath)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new IntakeValidationException([new("leadVendorId", "MISSING_REQUIRED_FIELD", "Tiger Lead ID is required before sending.")]);
        if (value.Length > 255 || value.Any(char.IsControl))
            throw new IntakeValidationException([new("leadVendorId", "INVALID_IDENTIFIER", "Tiger Lead ID must be a single-line identifier of at most 255 characters.")]);
        return value;
    }

    public IntakeRequest Map(SourceCase source) => Map(source, LeadVendorId(source));

    public HashSet<string> SourcePaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(FieldMapping mapping)
        {
            if (!mapping.Enabled) return;
            if (!string.IsNullOrWhiteSpace(mapping.UdfPath)) result.Add(mapping.UdfPath);
            foreach (var condition in mapping.WhenAll.Where(c => !string.IsNullOrWhiteSpace(c.UdfPath))) result.Add(condition.UdfPath);
            foreach (var part in mapping.Parts) Add(part);
        }
        foreach (var mapping in options.Mappings) Add(mapping);
        if (!string.IsNullOrWhiteSpace(options.LeadVendorIdUdfPath)) result.Add(options.LeadVendorIdUdfPath);
        return result;
    }

    public IntakeRequest Map(SourceCase source, string leadVendorId)
    {
        var request = new IntakeRequest { LeadVendorId = leadVendorId };
        List<FieldError> errors = [];
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in options.Mappings)
        {
            try
            {
                mapping.ValidateExclusion(narrativePart: false);
                var target = typeof(IntakeRequest).GetProperties().SingleOrDefault(p => string.Equals(p.Name, mapping.Destination, StringComparison.OrdinalIgnoreCase));
                if (target is null || target.Name is nameof(IntakeRequest.LeadVendorId) or nameof(IntakeRequest.Description) || !destinations.Add(mapping.Destination))
                    throw new InvalidOperationException("Destination must be a unique, supported API field; leadVendorId is managed by the sync.");
                var value = Resolve(source, mapping);
                if (value is null)
                {
                    if (mapping.Required) errors.Add(new(mapping.Destination, "MISSING_SOURCE", "Required source value is absent."));
                    continue;
                }
                if (target.PropertyType == typeof(string[]))
                {
                    var values = value.StartsWith('[') ? System.Text.Json.JsonSerializer.Deserialize<string[]>(value) : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (values is null || values.Any(v => v is null)) throw new InvalidOperationException("Multi-select choices must be nonnull strings.");
                    target.SetValue(request, values.Select(v => Translate(mapping, v)).ToArray());
                }
                else
                {
                    value = Translate(mapping, value);
                    if (IntakeValidator.DateFields.Contains(mapping.Destination))
                    {
                        if (!DateOnly.TryParseExact(value, ["yyyy-MM-dd", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                            throw new InvalidOperationException("Use yyyy-MM-dd or MM/dd/yyyy for date values.");
                        value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    target.SetValue(request, value);
                }
            }
            catch (IntakeValidationException exception) { errors.AddRange(exception.Errors); }
            catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
            { errors.Add(new(mapping.Destination, "MAPPING_ERROR", exception is System.Text.Json.JsonException ? "Invalid multi-select JSON array." : exception.Message)); }
        }
        errors.AddRange(new IntakeValidator(options).Validate(request));
        if (errors.Count > 0) throw new IntakeValidationException(errors, request);
        return request;
    }

    private static string? Resolve(SourceCase source, FieldMapping mapping)
    {
        string? value;
        if (mapping.Parts.Count > 0)
        {
            if (HasScalarSource(mapping)) throw new InvalidOperationException("Choose a scalar source or narrative Parts, not both.");
            if (mapping.Parts.Select(p => p.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Parts.Count)
                throw new InvalidOperationException("Narrative part labels must be distinct.");
            List<string> lines = [];
            List<FieldError> errors = [];
            foreach (var part in mapping.Parts)
            {
                try
                {
                    if (part.Parts.Count > 0 || !string.IsNullOrWhiteSpace(part.Destination))
                        throw new InvalidOperationException("Narrative parts must be scalar sources without a destination.");
                    if (string.IsNullOrWhiteSpace(part.Label)) throw new InvalidOperationException("Narrative parts require distinct, descriptive labels.");
                    part.ValidateExclusion(narrativePart: true);
                    if (!part.Enabled) continue;
                    var answer = Scalar(source, part);
                    if (!Applies(source, part.WhenAll, answer)) continue;
                    if (answer is null)
                    {
                        if (part.Required) throw new InvalidOperationException("Required applicable answer is absent.");
                        continue;
                    }
                    lines.Add(part.Label + ": " + Translate(part, answer));
                }
                catch (IntakeValidationException exception) { errors.AddRange(exception.Errors); }
                catch (InvalidOperationException exception) { errors.Add(new(mapping.Destination + "/" + part.Label, "MAPPING_ERROR", exception.Message)); }
            }
            if (errors.Count > 0) throw new IntakeValidationException(errors);
            value = Blank(string.Join(mapping.JoinWith, lines));
        }
        else value = Scalar(source, mapping);
        return Applies(source, mapping.WhenAll, value) ? value : null;
    }

    private static bool HasScalarSource(FieldMapping mapping) => !string.IsNullOrWhiteSpace(mapping.StandardProperty)
        || mapping.StandardProperties.Count > 0 || !string.IsNullOrWhiteSpace(mapping.SourceField) || !string.IsNullOrWhiteSpace(mapping.UdfPath);

    private static string? Scalar(SourceCase source, FieldMapping mapping)
    {
        if (mapping.Precedence is not ("RejectConflict" or "PreferUdf" or "PreferStandard")) throw new InvalidOperationException("Invalid mapping precedence.");
        var standardCount = (string.IsNullOrWhiteSpace(mapping.StandardProperty) ? 0 : 1)
            + (mapping.StandardProperties.Count == 0 ? 0 : 1) + (string.IsNullOrWhiteSpace(mapping.SourceField) ? 0 : 1);
        if (standardCount > 1) throw new InvalidOperationException("Choose one standard property, joined properties, or enriched source field.");
        if (!HasScalarSource(mapping)) throw new InvalidOperationException("A source binding is required.");
        string? standard = null;
        if (!string.IsNullOrWhiteSpace(mapping.SourceField)) standard = Enrichment(source, mapping.SourceField);
        if (!string.IsNullOrWhiteSpace(mapping.StandardProperty))
        {
            var property = typeof(SourceCase).GetProperty(mapping.StandardProperty) ?? throw new InvalidOperationException("Unknown standard property.");
            standard = property.GetValue(source) switch
            {
                null => null, DateTimeOffset date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                string text => text, _ => throw new InvalidOperationException("Only string/date standard properties can be mapped.")
            };
        }
        if (mapping.StandardProperties.Count > 0)
        {
            var parts = mapping.StandardProperties.Select(name =>
            {
                var property = typeof(SourceCase).GetProperty(name) ?? throw new InvalidOperationException("Unknown standard property.");
                if (property.PropertyType != typeof(string)) throw new InvalidOperationException("Joined standard properties must be strings.");
                return Blank(property.GetValue(source) as string);
            }).Where(value => value != null);
            standard = string.Join(mapping.JoinWith, parts);
        }
        standard = Blank(standard);
        var udf = string.IsNullOrWhiteSpace(mapping.UdfPath) ? null : Blank(Udf(source, mapping.UdfPath));
        if (standard != null && udf != null && standard != udf && mapping.Precedence == "RejectConflict")
            throw new InvalidOperationException("Standard and UDF values conflict; configure explicit precedence.");
        return mapping.Precedence == "PreferStandard" ? standard ?? udf : udf ?? standard;
    }

    private static bool Applies(SourceCase source, List<SourceCondition> conditions, string? childValue)
    {
        bool missing = false;
        foreach (var condition in conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.UdfPath) == string.IsNullOrWhiteSpace(condition.SourceField))
                throw new InvalidOperationException("Each condition requires exactly one UDF path or enriched source field.");
            var parent = Blank(string.IsNullOrWhiteSpace(condition.UdfPath) ? Enrichment(source, condition.SourceField) : Udf(source, condition.UdfPath));
            if (parent is null) { missing = true; continue; }
            if (!string.Equals(parent, condition.EqualsValue, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (missing && childValue != null) throw new InvalidOperationException("A populated conditional answer has an unanswered parent; review its applicability.");
        return !missing;
    }
    private static string? Enrichment(SourceCase source, string key)
    {
        var enriched = source;
        if (enriched.SourceFields is null) throw new InvalidOperationException("Enriched source fields are absent; reload the complete source snapshot.");
        var matches = enriched.SourceFields.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Enriched source field is missing or ambiguous; verify the loader binding.");
        return matches[0].Value;
    }
    private static string Translate(FieldMapping mapping, string item)
    {
        if (mapping.Values.Count == 0) return item;
        var match = mapping.Values.SingleOrDefault(p => string.Equals(p.Key, item, StringComparison.OrdinalIgnoreCase));
        return match.Value ?? throw new InvalidOperationException("Source value has no configured translation.");
    }
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
