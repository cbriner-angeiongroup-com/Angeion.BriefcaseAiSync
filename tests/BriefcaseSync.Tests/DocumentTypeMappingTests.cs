using System.Text.Json;
using BriefcaseSync.Core;
using BriefcaseSync.Infrastructure;
using BriefcaseSync.Web.Services;
using Xunit;

namespace BriefcaseSync.Tests;

public sealed class DocumentTypeMappingTests
{
    [Fact]
    public void InitialMappingsAreExplicitExactPairsForAllSixTypes()
    {
        var mappings = DocumentTypes.ExactMatches();
        Assert.Equal(6, mappings.Count);
        foreach (var type in DocumentTypes.Accepted)
            Assert.Equal(type, DocumentTypes.Resolve(type, mappings));
    }

    [Fact]
    public void SourceNamesCanDifferFromDestinationNames()
    {
        var mappings = new List<DocumentTypeMapping>
        {
            new() { SourceType = "SA Signed Retainer Agreement", DocumentType = "Signed Retainer Agreement" }
        };
        Assert.Equal("Signed Retainer Agreement", DocumentTypes.Resolve("SA Signed Retainer Agreement", mappings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Unknown")]
    [InlineData("signed retainer agreement")]
    public void MissingAndUnmappedValuesNeverUseADefault(string? sourceType) =>
        Assert.Throws<InvalidOperationException>(() => DocumentTypes.Resolve(sourceType, DocumentTypes.ExactMatches()));

    [Fact]
    public void DuplicateSourcesAndUnsupportedDestinationsAreRejected()
    {
        var mappings = DocumentTypes.ExactMatches();
        mappings.Add(new() { SourceType = mappings[0].SourceType, DocumentType = "Other" });
        Assert.Throws<InvalidOperationException>(() => DocumentTypes.Validate(mappings));
        mappings.RemoveAt(mappings.Count - 1);
        mappings[0].DocumentType = "Briefcase Signed Retainer";
        Assert.Throws<InvalidOperationException>(() => DocumentTypes.Validate(mappings));
        mappings[0].DocumentType = "";
        Assert.Throws<InvalidOperationException>(() => DocumentTypes.Validate(mappings));
    }

    [Fact]
    public async Task SettingsRoundTripMappingsWithoutSharingDrafts()
    {
        var defaults = new AppOptions { Mapping = Fixtures.Mapping() };
        var draft = RuntimeSettings.From(defaults);
        draft.DocumentTypeMappings[0].SourceType = "SA Signed Retainer Agreement";
        Assert.Equal("Signed Retainer Agreement", defaults.DocumentTypeMappings[0].SourceType);
        var repository = new DemoOperationsRepository(defaults);
        var current = await repository.LoadSettings(defaults, default);
        var saved = await repository.SaveSettings(draft, current.Version, "synthetic-operator", default);
        var restored = JsonSerializer.Deserialize<RuntimeSettings>(JsonSerializer.Serialize(saved.Settings))!;
        var worker = new AppOptions(); restored.Apply(worker);
        restored.DocumentTypeMappings[0].SourceType = "unsaved edit";
        Assert.Equal("SA Signed Retainer Agreement", worker.DocumentTypeMappings[0].SourceType);
        Assert.Equal("SA Signed Retainer Agreement", (await repository.LoadSettings(defaults, default)).Settings.DocumentTypeMappings[0].SourceType);
        Assert.False(worker.SendingEnabled);
        Assert.False(worker.MappingApproved);
    }
}
