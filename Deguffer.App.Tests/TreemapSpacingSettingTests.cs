using System.Xml.Linq;
using Deguffer.App.Views;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Settings page and the map appearance window offer one stored spacing. A reader who changes it
/// in one place has to recognise it in the other, so both rows are drawn from
/// <see cref="TreemapSpacingSetting"/> rather than worded twice, which is how they drifted apart.
/// </summary>
public sealed class TreemapSpacingSettingTests
{
    /// <summary>The markup of each place the spacing is offered.</summary>
    private static readonly string[] Offered = ["Deguffer.App/Views/SettingsPage.xaml", "Deguffer.App/Views/MapDetailSection.xaml"];

    private const string Words = "views:TreemapSpacingSetting";

    [Theory]
    [MemberData(nameof(Places))]
    public void EachPlaceTakesItsIconWordsAndChoicesFromTheSetting(string markup)
    {
        var rows = XDocument.Load(Path.Combine(SourceTree.RepositoryRoot, markup))
            .Descendants()
            .Where(element => element.Name.LocalName == "SettingsRow"
                && (string?)element.Attribute("Title") == $"{{x:Bind {Words}.Title}}")
            .ToList();

        var row = Assert.Single(rows);
        var box = Assert.Single(row.Descendants(), element => element.Name.LocalName == "ComboBox");

        Assert.Equal($"{{x:Bind {Words}.Glyph}}", (string?)row.Attribute("Glyph"));
        Assert.Equal($"{{x:Bind {Words}.Description}}", (string?)row.Attribute("Description"));
        Assert.Equal($"{{x:Bind {Words}.Choices}}", (string?)box.Attribute("ItemsSource"));
        Assert.Equal($"{{x:Bind {Words}.Title}}", (string?)box.Attribute("AutomationProperties.Name"));
        Assert.Empty(box.Elements());
    }

    public static TheoryData<string> Places() => [.. Offered];

    /// <summary>The design rule's budget, which cannot read a description the markup binds.</summary>
    [Fact]
    public void TheDescriptionIsOneShortLine() =>
        Assert.InRange(TreemapSpacingSetting.Description.Length, 1, 140);

    /// <summary>A box's index is the stored spacing, so there is one choice for each, in its order.</summary>
    [Fact]
    public void ThereIsOneChoiceForEachSpacing() =>
        Assert.Equal(Enum.GetValues<ExploreSpacing>().Length, TreemapSpacingSetting.Choices.Count);
}
