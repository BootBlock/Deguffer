using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the map appearance window holds the look, the page that draws it and the preferences file
/// together: a change is shown before it is written, only the setting that changed is written, and a
/// spacing chosen on the Settings page is taken up without undoing one this window chose. What a look
/// is and which picture a scheme belongs to is <see cref="MapLook"/>'s, and proven in Core.
/// </summary>
public sealed class MapAppearanceViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly PreferenceService _preferences;

    public MapAppearanceViewModelTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _preferences = new PreferenceService(new PreferenceStore(_environment));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The window as the Explore page builds it: from the preferences as they stood when the page was built.</summary>
    private MapAppearanceViewModel Window() => new(MapLook.From(_preferences.Current), _preferences);

    /// <summary>A folder where the file goes, so every write from here is refused as a locked profile refuses it.</summary>
    private void RefuseWrites() =>
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));

    /// <summary>A list with nothing selected reports -1 while it rebuilds, which is not a choice.</summary>
    [Fact]
    public void AListWithNothingSelectedChangesNothing()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;
        var look = window.Look;

        window.SchemeIndex = -1;
        window.SpacingIndex = -1;

        Assert.Equal(0, changes);
        Assert.Equal(look, window.Look);
        Assert.Equal(AppPreferences.Default, _preferences.Current);
    }

    /// <summary>
    /// The list and the tree draw no picture, and the map behind them is left holding the treemap's
    /// drawing, so a scheme chosen there is the treemap's. The page is told once.
    /// </summary>
    [Fact]
    public void ASchemeChosenIsShownOnceAndWrittenForThePictureOnScreen()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.View = ExploreView.Tree;
        window.SchemeIndex = (int)ExploreScheme.Deep;

        Assert.Equal(1, changes);
        Assert.Equal(ExploreScheme.Deep, window.Look.Treemap);
        Assert.Equal(ExploreScheme.Deep, _preferences.Current.TreemapScheme);
        Assert.Equal(ExploreScheme.Standard, _preferences.Current.IcicleScheme);
        Assert.Equal("Colours for the treemap", window.SchemeHeading);
    }

    /// <summary>
    /// The window can stay open while the spacing is chosen on the Settings page, and writing every
    /// field would put back what this window last knew over what the reader has since chosen there.
    /// </summary>
    [Fact]
    public void ASchemeWrittenLeavesASpacingChosenElsewhereAlone()
    {
        var window = Window();

        _preferences.Update(current => current with { TreemapSpacing = ExploreSpacing.Spacious });
        window.SchemeIndex = (int)ExploreScheme.Soft;

        Assert.Equal(ExploreSpacing.Spacious, _preferences.Current.TreemapSpacing);
        Assert.Equal(ExploreScheme.Soft, _preferences.Current.TreemapScheme);
    }

    /// <summary>A file that could not be written is no reason to show the reader the old colours.</summary>
    [Fact]
    public void AChangeThatCannotBeWrittenIsStillShown()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;
        RefuseWrites();

        window.SchemeIndex = (int)ExploreScheme.Vivid;

        Assert.Equal(1, changes);
        Assert.Equal(ExploreScheme.Vivid, window.Look.Treemap);
        Assert.Equal(ExploreScheme.Standard, _preferences.Current.TreemapScheme);
    }

    /// <summary>
    /// A spacing this window chose and could not write leaves the preferences where they were, and
    /// following them back on the next visit would undo the reader's choice.
    /// </summary>
    [Fact]
    public void ASpacingThatCouldNotBeWrittenIsNotUndoneOnTheNextVisit()
    {
        var window = Window();
        RefuseWrites();

        window.SpacingIndex = (int)ExploreSpacing.Dense;
        window.Follow(_preferences.Current.TreemapSpacing);

        Assert.Equal(ExploreSpacing.Comfortable, _preferences.Current.TreemapSpacing);
        Assert.Equal(ExploreSpacing.Dense, window.Look.Spacing);
    }

    /// <summary>
    /// A spacing this window wrote is what it last heard the preferences hold, so a visit that finds
    /// them unchanged since is not taken for a choice made on the Settings page.
    /// </summary>
    [Fact]
    public void ASpacingThatWasWrittenIsWhatTheNextVisitExpects()
    {
        var window = Window();
        var changes = 0;

        window.SpacingIndex = (int)ExploreSpacing.Dense;
        window.Changed += (_, _) => changes++;
        window.Follow(ExploreSpacing.Dense);

        Assert.Equal(0, changes);
        Assert.Equal(ExploreSpacing.Dense, _preferences.Current.TreemapSpacing);
    }

    /// <summary>A spacing chosen on the Settings page while the Explore page was away is taken up, and not written back.</summary>
    [Fact]
    public void ASpacingChosenOnTheSettingsPageIsTakenUp()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;
        var raised = new List<string?>();
        window.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        window.Follow(ExploreSpacing.Spacious);

        Assert.Equal(1, changes);
        Assert.Equal(ExploreSpacing.Spacious, window.Look.Spacing);
        Assert.Contains(nameof(MapAppearanceViewModel.SpacingIndex), raised);
        Assert.Equal(ExploreSpacing.Comfortable, _preferences.Current.TreemapSpacing);
    }

    /// <summary>
    /// The list is bound by index, so an option's place is which scheme it is. Its strips are the
    /// colours the map draws, and the age strip leaves out the grey for an undated entry.
    /// </summary>
    [Fact]
    public void EachSchemeIsListedInItsOwnPlaceWithTheColoursTheMapDraws()
    {
        var window = Window();

        Assert.Equal(Enum.GetValues<ExploreScheme>().Length, window.Schemes.Count);

        foreach (var scheme in Enum.GetValues<ExploreScheme>())
        {
            var option = window.Schemes[(int)scheme];
            var bands = AgePalette.Bands(scheme);

            Assert.Equal(bands.Count - 1, option.Ages.Count);
            Assert.Equal(bands[0].Colour, option.Ages[0].Tile);
            Assert.Equal(TilePalette.For(new BranchHue(0, 30, false), 1, scheme), option.Branches[0].Tile);
            Assert.Contains(option.Name, option.Spoken, StringComparison.Ordinal);
        }

        Assert.Equal("Deep", window.Schemes[(int)ExploreScheme.Deep].Name);
    }

    /// <summary>A swatch is painted opaque, in exactly the colour the map's palette states.</summary>
    [Fact]
    public void ASwatchIsPaintedInThePalettesOwnColour()
    {
        var swatch = new MapSwatch(new TileColour(12, 34, 56));

        Assert.Equal((byte)255, swatch.Colour.A);
        Assert.Equal((12, 34, 56), (swatch.Colour.R, swatch.Colour.G, swatch.Colour.B));
    }
}
