using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The part of the preferences the map appearance window changes. What is asserted is that a
/// scheme chosen for one view reaches that view and no other, because the window offers one scheme
/// picker and it follows whichever view the page is on.
/// </summary>
public sealed class MapLookTests
{
    private static readonly MapLook Standard = MapLook.From(AppPreferences.Default);

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void ASchemeChosenForOnePictureChangesThatPictureAlone(ExploreView view)
    {
        var look = Standard.WithScheme(view, ExploreScheme.Deep);

        foreach (var other in new[] { ExploreView.Treemap, ExploreView.Icicle, ExploreView.Sunburst })
        {
            Assert.Equal(other == view ? ExploreScheme.Deep : ExploreScheme.Standard, look.SchemeFor(other));
        }
    }

    /// <summary>
    /// The list and the tree draw no picture, and the map behind them holds the treemap. A scheme
    /// chosen while either is showing is the treemap's, which is the drawing the reader returns to.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.List)]
    [InlineData(ExploreView.Tree)]
    public void AViewWithNoPictureStandsForTheTreemap(ExploreView view)
    {
        var look = Standard.WithScheme(view, ExploreScheme.Vivid);

        Assert.Equal(ExploreScheme.Vivid, look.Treemap);
        Assert.Equal(ExploreScheme.Vivid, look.SchemeFor(view));
        Assert.Equal(ExploreView.Treemap, MapLook.Drawn(view));
    }

    /// <summary>
    /// A number no member names, which is what a hand-edited file gives: the store reads enums with
    /// numbers allowed. It is drawn as the default rather than handed to a palette it indexes past.
    /// </summary>
    [Fact]
    public void AValueNoMemberNamesIsReadAsTheDefault()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(
            Path.Combine(directory.FullName, "preferences.json"),
            """{ "TreemapSpacing": 9, "TreemapScheme": 9, "IcicleScheme": -1, "SunburstScheme": "Deep" }""");

        var look = MapLook.From(new PreferenceStore(environment).Load());

        Assert.Equal(new MapLook(ExploreSpacing.Comfortable, ExploreScheme.Standard, ExploreScheme.Standard, ExploreScheme.Deep), look);
    }

    /// <summary>
    /// Written back into the preferences, the look replaces its own four settings and leaves every
    /// other one as it was, including the ones that govern what is deleted.
    /// </summary>
    [Fact]
    public void WritingTheLookBackChangesNothingElse()
    {
        var before = AppPreferences.Default with { ConfirmBeforeCleaning = false, KeepFilesChangedWithinHours = 4 };
        var look = new MapLook(ExploreSpacing.Dense, ExploreScheme.Vivid, ExploreScheme.Soft, ExploreScheme.Deep);

        var after = look.Into(before);

        Assert.Equal(look, MapLook.From(after));
        Assert.Equal(
            before with
            {
                TreemapSpacing = ExploreSpacing.Dense,
                TreemapScheme = ExploreScheme.Vivid,
                IcicleScheme = ExploreScheme.Soft,
                SunburstScheme = ExploreScheme.Deep,
            },
            after);
    }
}
