using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Explore page opens on. What is asserted is that a stored value the page's View or Colour
/// box cannot show is read as the default, because the page selects it in that box as it is built
/// and an index past the box's items throws there.
/// </summary>
public sealed class ExploreChoiceTests
{
    /// <summary>
    /// A number no member names, which is what a hand-edited file gives: the store reads enums with
    /// numbers allowed.
    /// </summary>
    [Theory]
    [InlineData("9", "7")]
    [InlineData("-1", "-2")]
    public void AValueNoMemberNamesIsReadAsTheDefault(string view, string colouring)
    {
        var choice = ExploreChoice.From(Load($$"""{ "Explore": {{view}}, "ExploreColours": {{colouring}} }"""));

        Assert.Equal(new ExploreChoice(ExploreView.Treemap, ExploreColouring.Branch), choice);
    }

    /// <summary>The tree is a view the Memory page offers and this page does not.</summary>
    [Fact]
    public void AViewThisPageDoesNotOfferIsReadAsTheTreemap()
    {
        var choice = ExploreChoice.From(AppPreferences.Default with { Explore = ExploreView.Tree });

        Assert.Equal(ExploreView.Treemap, choice.View);
        Assert.Equal(0, ExploreChoice.IndexOf(ExploreView.Tree));
    }

    [Fact]
    public void AStoredChoiceThePageOffersIsKept()
    {
        var choice = ExploreChoice.From(Load("""{ "Explore": "Files", "ExploreColours": "Depth" }"""));

        Assert.Equal(new ExploreChoice(ExploreView.Files, ExploreColouring.Depth), choice);
    }

    [Fact]
    public void EveryOfferedViewIsFoundInItsOwnPlace()
    {
        for (var i = 0; i < ExploreChoice.Views.Count; i++)
        {
            Assert.Equal(i, ExploreChoice.IndexOf(ExploreChoice.Views[i]));
        }
    }

    private static AppPreferences Load(string json)
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(Path.Combine(directory.FullName, "preferences.json"), json);

        return new PreferenceStore(environment).Load();
    }
}
