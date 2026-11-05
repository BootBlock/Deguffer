using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;

namespace Deguffer.App.Tests;

/// <summary>The search settings' one line: where it looks, what makes a match, and each filter that narrows it.</summary>
public sealed class DuplicateSearchSummaryTests
{
    [Fact]
    public void TheDefaultsNameTheContentAndItsChecksum() =>
        Assert.Equal(
            @"No drive or folder chosen · matched on the content (XXH128)",
            DuplicateSearchSummary.Of([], DuplicatePreferences.Default));

    [Fact]
    public void SeveralLocationsAreCountedWithTheirReferences() =>
        Assert.Equal(
            @"C:\ and 2 more · 1 reference · matched on the name and size",
            DuplicateSearchSummary.Of(
                [new(@"C:\"), new(@"D:\Photos", LocationRole.Reference), new(@"E:\")],
                DuplicatePreferences.Default with { Criteria = MatchCriteria.Name | MatchCriteria.Size }));

    [Fact]
    public void TwoLocationsAreBothNamed() =>
        Assert.StartsWith(
            @"C:\ and D:\Photos · ",
            DuplicateSearchSummary.Of([new(@"C:\"), new(@"D:\Photos")], DuplicatePreferences.Default),
            StringComparison.Ordinal);

    [Theory]
    [InlineData(1.0, null, " · 1.0 MB or larger")]
    [InlineData(null, 2.0, " · up to 2.0 MB")]
    [InlineData(1.0, 2.0, " · 1.0 MB to 2.0 MB")]
    public void ASizeLimitIsSaid(double? smallest, double? largest, string said) =>
        Assert.EndsWith(
            said,
            DuplicateSearchSummary.Of(
                [new(@"C:\")],
                DuplicatePreferences.Default with
                {
                    Smallest = new SizeLimit(smallest, SizeUnit.MB),
                    Largest = new SizeLimit(largest, SizeUnit.MB),
                }),
            StringComparison.Ordinal);

    [Theory]
    [InlineData(ExtensionFilterMode.OnlyThese, "jpg, png", " · only .jpg, .png")]
    [InlineData(ExtensionFilterMode.SkipThese, "tmp", " · not .tmp")]
    public void AnExtensionFilterIsSaid(ExtensionFilterMode mode, string typed, string said) =>
        Assert.EndsWith(
            said,
            DuplicateSearchSummary.Of([new(@"C:\")], DuplicatePreferences.Default with { ExtensionMode = mode, Extensions = typed }),
            StringComparison.Ordinal);

    /// <summary>A filter that reads nothing narrows nothing, so the line does not claim one.</summary>
    [Theory]
    [InlineData(ExtensionFilterMode.Any, "jpg")]
    [InlineData(ExtensionFilterMode.SkipThese, "")]
    [InlineData(ExtensionFilterMode.OnlyThese, "")]
    public void AnExtensionFilterThatReadsNothingIsNotSaid(ExtensionFilterMode mode, string typed) =>
        Assert.Equal(
            @"C:\ · matched on the content (XXH128)",
            DuplicateSearchSummary.Of([new(@"C:\")], DuplicatePreferences.Default with { ExtensionMode = mode, Extensions = typed }));
}
