using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Explore page opens on. What is asserted is that a stored value the page's view switch or
/// Colour by box cannot show is read as the default, because the page selects it there as it is built
/// and an index past the items throws.
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

    /// <summary>
    /// The Colour by box lists every colouring, each at its own place, and a number no member names
    /// is placed where the branch is rather than past the box's items.
    /// </summary>
    [Fact]
    public void EveryColouringHasAPlaceInTheBox()
    {
        Assert.Equal(Enum.GetValues<ExploreColouring>().Order(), ExploreChoice.Colourings.Order());

        for (var i = 0; i < ExploreChoice.Colourings.Count; i++)
        {
            Assert.Equal(i, ExploreChoice.IndexOf(ExploreChoice.Colourings[i]));
        }

        Assert.Equal(0, ExploreChoice.IndexOf((ExploreColouring)9));
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
