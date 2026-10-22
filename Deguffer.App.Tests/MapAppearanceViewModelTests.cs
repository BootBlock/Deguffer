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

    public void Dispose()
    {
        // A file locked by RefuseWrites is let go of, so the folder can be removed.
        if (File.Exists(PreferencesFile))
        {
            File.SetAttributes(PreferencesFile, FileAttributes.Normal);
        }

        _temp.Dispose();
    }

    private string PreferencesFile => Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json");

    /// <summary>The window as the Explore page builds it: from the preferences as they stood when the page was built.</summary>
    private MapAppearanceViewModel Window() => new(MapLook.From(_preferences.Current), _preferences);

    /// <summary>
    /// Every write from here refused, as a locked profile refuses it: a folder where the file goes, or
    /// where a write has already made the file, the file made read-only.
    /// </summary>
    private void RefuseWrites()
    {
        if (File.Exists(PreferencesFile))
        {
            File.SetAttributes(PreferencesFile, FileAttributes.ReadOnly);
        }
        else
        {
            Directory.CreateDirectory(PreferencesFile);
        }
    }

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

    /// <summary>
    /// Each box of the style is shown at once and writes its own setting and no other: a line chosen
    /// round the blocks leaves a shading chosen in another write, and every other setting, as it was.
    /// </summary>
    [Fact]
    public void EachPartOfTheStyleIsShownAndWrittenOnItsOwn()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.ShadingIndex = (int)MapShading.Bevel;
        _preferences.Update(current => current with { MapStyle = current.MapStyle with { FolderFill = MapFolderFill.Grey } });
        window.BlockBordersIndex = (int)MapBorder.Strong;
        window.FolderBordersIndex = (int)MapBorder.Faint;
        window.BorderWidthIndex = (int)MapBorderWidth.Thick;
        window.BorderToneIndex = (int)MapBorderTone.Light;
        window.ReliefIndex = (int)MapRelief.Subtle;
        window.FolderNames = false;
        window.ShapeNames = false;

        var expected = new MapStyle(
            MapShading.Bevel,
            MapRelief.Subtle,
            MapBorder.Strong,
            MapBorder.Faint,
            MapBorderWidth.Thick,
            MapBorderTone.Light,
            MapFolderFill.Coloured,
            FolderNames: false,
            ShapeNames: false);

        Assert.Equal(8, changes);
        Assert.Equal(expected, window.Look.Style);
        Assert.Equal(expected with { FolderFill = MapFolderFill.Grey }, _preferences.Current.MapStyle);
        Assert.Equal(AppPreferences.Default with { MapStyle = _preferences.Current.MapStyle }, _preferences.Current);

        window.FolderFillIndex = (int)MapFolderFill.Grey;

        Assert.Equal(MapFolderFill.Grey, window.Look.Style.FolderFill);
        Assert.Equal(9, changes);
    }

    /// <summary>
    /// A box with nothing selected reports -1 while it rebuilds, which is not a choice, for the style's
    /// boxes as for the others.
    /// </summary>
    [Fact]
    public void AStyleBoxWithNothingSelectedChangesNothing()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.ShadingIndex = -1;
        window.ReliefIndex = -1;
        window.BlockBordersIndex = -1;
        window.FolderBordersIndex = -1;
        window.BorderWidthIndex = -1;
        window.BorderToneIndex = -1;
        window.FolderFillIndex = -1;
        window.PresetIndex = -1;

        Assert.Equal(0, changes);
        Assert.Equal(AppPreferences.Default, _preferences.Current);
    }

    /// <summary>
    /// A preset is shown and written as one change, takes the place of the spacing, keeps each view's
    /// scheme, and hands the page the colouring that is part of its look.
    /// </summary>
    [Fact]
    public void APresetIsShownAndWrittenAsOneChangeAndHandsOverItsColouring()
    {
        var window = Window();
        var changes = 0;
        var colourings = new List<ExploreColouring>();
        window.Changed += (_, _) => changes++;
        window.ColouringChosen += (_, colouring) => colourings.Add(colouring);

        window.SchemeIndex = (int)ExploreScheme.Soft;
        window.PresetIndex = MapPreset.All.ToList().IndexOf(MapPreset.WizTree);

        Assert.Equal(2, changes);
        Assert.Equal([ExploreColouring.Type], colourings);
        Assert.Equal(MapPreset.WizTree.Style, window.Look.Style);
        Assert.Equal(ExploreSpacing.Dense, window.Look.Spacing);
        Assert.Equal(ExploreScheme.Soft, window.Look.Treemap);
        Assert.Equal(MapPreset.WizTree.Style, _preferences.Current.MapStyle);
        Assert.Equal(ExploreSpacing.Dense, _preferences.Current.TreemapSpacing);
        Assert.Equal(ExploreScheme.Soft, _preferences.Current.TreemapScheme);
        Assert.Equal(MapPreset.All.ToList().IndexOf(MapPreset.WizTree), window.PresetIndex);
        Assert.Contains("WizTree", window.PresetSummary, StringComparison.Ordinal);

        // Its spacing is what the next visit expects, as a spacing chosen in its own box is: a spacing
        // chosen after it and not written is not undone by the file still holding the preset's.
        RefuseWrites();
        window.SpacingIndex = (int)ExploreSpacing.Spacious;
        window.Follow(_preferences.Current.TreemapSpacing);

        Assert.Equal(ExploreSpacing.Dense, _preferences.Current.TreemapSpacing);
        Assert.Equal(ExploreSpacing.Spacious, window.Look.Spacing);
    }

    /// <summary>
    /// A preset without a colouring of its own leaves the page's alone, and choosing again the preset
    /// the map is already drawn as still hands over its colouring: the reader is asking for all of it.
    /// </summary>
    [Fact]
    public void APresetHandsOverOnlyAColouringItHas()
    {
        var window = Window();
        var colourings = new List<ExploreColouring>();
        window.ColouringChosen += (_, colouring) => colourings.Add(colouring);

        window.PresetIndex = MapPreset.All.ToList().IndexOf(MapPreset.Flat);
        window.PresetIndex = 0;
        window.PresetIndex = 0;

        Assert.Equal([ExploreColouring.Branch, ExploreColouring.Branch], colourings);
    }

    /// <summary>
    /// A change after a preset makes a look of the reader's own, which no preset describes, and the
    /// window says so. Every box is told, so each shows the look as it now stands.
    /// </summary>
    [Fact]
    public void AChangeAfterAPresetIsALookOfTheReadersOwn()
    {
        var window = Window();
        var raised = new List<string?>();

        Assert.Equal(0, window.PresetIndex);

        window.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);
        window.BorderToneIndex = (int)MapBorderTone.Light;

        Assert.Equal(-1, window.PresetIndex);
        Assert.Contains("of your own", window.PresetSummary, StringComparison.Ordinal);
        Assert.Contains(nameof(MapAppearanceViewModel.PresetIndex), raised);
        Assert.Contains(nameof(MapAppearanceViewModel.ShadingIndex), raised);
        Assert.Contains(nameof(MapAppearanceViewModel.FolderNames), raised);
    }

    /// <summary>A preset that could not be written is still shown, as any other change is.</summary>
    [Fact]
    public void APresetThatCannotBeWrittenIsStillShown()
    {
        var window = Window();
        RefuseWrites();

        window.PresetIndex = MapPreset.All.ToList().IndexOf(MapPreset.WizTree);

        Assert.Equal(MapPreset.WizTree.Style, window.Look.Style);
        Assert.Equal(MapStyle.Default, _preferences.Current.MapStyle);

        // Its spacing did not reach the file, so the next visit does not take the file's back over it.
        window.Follow(_preferences.Current.TreemapSpacing);
        Assert.Equal(ExploreSpacing.Comfortable, _preferences.Current.TreemapSpacing);
        Assert.Equal(MapPreset.WizTree.Spacing, window.Look.Spacing);
    }

    /// <summary>The legend of a map coloured by depth names each of its eight levels in the colours the map draws.</summary>
    [Fact]
    public void TheDepthLegendNamesEachLevelInItsColour()
    {
        var bands = ExploreLegendBand.For(ExploreColouring.Depth, ExploreScheme.Standard);

        Assert.Equal(DepthPalette.Bands.Select(band => band.Label), bands.Select(band => band.Label));
        Assert.Equal(DepthPalette.For(3), new TileColour(bands[3].Swatch.R, bands[3].Swatch.G, bands[3].Swatch.B));
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
