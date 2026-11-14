using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the appearance window's sections and its newer boxes are wired to the look: each box is shown
/// at once, writes its own setting and no other, survives a restart, ignores a list rebuilding, and
/// tells the boxes that depend on it. What the choices draw is proven in Core.
/// </summary>
public sealed class MapAppearanceSectionsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly PreferenceService _preferences;

    public MapAppearanceSectionsTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _preferences = new PreferenceService(new PreferenceStore(_environment));
    }

    public void Dispose() => _temp.Dispose();

    private MapAppearanceViewModel Window() => new(_preferences);

    /// <summary>The preferences as the next launch of the application reads them from the file.</summary>
    private AppPreferences Relaunched() => new PreferenceStore(_environment).Load();

    /// <summary>
    /// Every new box of the style is shown at once and written as its own setting, leaving a part of the
    /// style written elsewhere in between as the file holds it, and reaches the next launch.
    /// </summary>
    [Fact]
    public void EachNewPartOfTheStyleIsShownWrittenAndKept()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.SaturationIndex = (int)MapSaturation.Muted;
        _preferences.Update(current => current with { MapStyle = current.MapStyle with { Shading = MapShading.Flat } });
        window.BrightnessIndex = (int)MapBrightness.Lighter;
        window.Shapes.BevelWidthIndex = (int)MapBevelWidth.Wide;

        var shown = MapStyle.Default with { Saturation = MapSaturation.Muted, Brightness = MapBrightness.Lighter, BevelWidth = MapBevelWidth.Wide };

        Assert.Equal(3, changes);
        Assert.Equal(shown, window.Look.Style);
        Assert.Equal(shown with { Shading = MapShading.Flat }, Relaunched().MapStyle);
        Assert.Equal(AppPreferences.Default with { MapStyle = shown with { Shading = MapShading.Flat } }, Relaunched());
    }

    /// <summary>Every box of the detail is shown at once, written as its own setting and nothing else, and reaches the next launch.</summary>
    [Fact]
    public void EachPartOfTheDetailIsShownWrittenAndKept()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.SmallestShapeIndex = (int)MapSmallestShape.Tiny;
        window.LevelsIndex = (int)MapLevels.More;
        window.Names.TextSizeIndex = (int)MapTextSize.Large;
        window.Names.NameCountIndex = (int)MapNameCount.Most;
        window.Names.Sizes = false;

        var expected = new MapDetail(MapSmallestShape.Tiny, MapLevels.More, MapTextSize.Large, MapNameCount.Most, Sizes: false);

        Assert.Equal(5, changes);
        Assert.Equal(expected, window.Look.Detail);
        Assert.Equal(AppPreferences.Default with { MapDetail = expected }, Relaunched());
        Assert.Equal(expected, new MapAppearanceViewModel(new PreferenceService(new PreferenceStore(_environment))).Look.Detail);
    }

    /// <summary>The light's reach is written beside its strength, and a box rebuilding leaves it.</summary>
    [Fact]
    public void TheLightsReachIsShownWrittenAndKept()
    {
        var window = Window();

        window.Shapes.LightReachIndex = -1;

        Assert.Equal(AppPreferences.Default, _preferences.Current);

        window.Shapes.LightReachIndex = (int)MapLightReach.Wide;

        Assert.Equal(MapLightReach.Wide, window.Look.Light.Reach);
        Assert.Equal(new MapPointerLight(Reach: MapLightReach.Wide), Relaunched().MapPointerLight);
    }

    /// <summary>A box with nothing selected reports -1 while it rebuilds, which is not a choice, for every new box.</summary>
    [Fact]
    public void ANewBoxWithNothingSelectedChangesNothing()
    {
        var window = Window();
        var changes = 0;
        window.Changed += (_, _) => changes++;

        window.SaturationIndex = -1;
        window.BrightnessIndex = -1;
        window.SmallestShapeIndex = -1;
        window.LevelsIndex = -1;
        window.Shapes.BevelWidthIndex = -1;
        window.Names.TextSizeIndex = -1;
        window.Names.NameCountIndex = -1;
        window.Names.LetteringIndex = -1;
        window.Lines.LinesIndex = -1;

        Assert.Equal(0, changes);
        Assert.Equal(AppPreferences.Default, _preferences.Current);
    }

    /// <summary>
    /// A preset keeps the detail the reader chose, as it keeps the light, and restoring the defaults
    /// puts the detail back with every other part of the look.
    /// </summary>
    [Fact]
    public void APresetKeepsTheDetailAndRestoringTheDefaultsPutsItBack()
    {
        var window = Window();

        window.LevelsIndex = (int)MapLevels.Most;
        window.PresetIndex = MapPreset.All.ToList().IndexOf(MapPreset.Pastel);

        Assert.Equal(MapLevels.Most, window.Look.Detail.Levels);
        Assert.Equal(MapPreset.Pastel.Style, window.Look.Style);
        Assert.Equal(MapPreset.All.ToList().IndexOf(MapPreset.Pastel), window.PresetIndex);
        Assert.Equal(MapLevels.Most, Relaunched().MapDetail.Levels);

        window.RestoreDefaults();

        Assert.Equal(MapLook.Default, window.Look);
        Assert.Equal(MapDetail.Default, Relaunched().MapDetail);
    }

    /// <summary>
    /// A change of look tells each section's boxes, so a box that depends on another greys or wakes as
    /// it moves: a bevel's boxes with the shading, the lines' with the first line drawn, and the
    /// names' with the lettering.
    /// </summary>
    [Fact]
    public void AChangeTellsTheBoxesThatDependOnIt()
    {
        var window = Window();
        var shapes = new List<string?>();
        var lines = new List<string?>();
        var names = new List<string?>();
        window.Shapes.PropertyChanged += (_, changed) => shapes.Add(changed.PropertyName);
        window.Lines.PropertyChanged += (_, changed) => lines.Add(changed.PropertyName);
        window.Names.PropertyChanged += (_, changed) => names.Add(changed.PropertyName);

        Assert.False(window.Shapes.Bevelled);
        Assert.False(window.Lines.Outlined);
        Assert.True(window.Names.OwnLettering);

        window.PresetIndex = MapPreset.All.ToList().IndexOf(MapPreset.SpaceMonger);

        Assert.True(window.Shapes.Bevelled);
        Assert.True(window.Lines.Outlined);
        Assert.False(window.Names.OwnLettering);
        Assert.False(window.Names.NamesCounted);
        Assert.Contains(nameof(MapShapesViewModel.Bevelled), shapes);
        Assert.Contains(nameof(MapLinesViewModel.Outlined), lines);
        Assert.Contains(nameof(MapNamesViewModel.OwnLettering), names);
        Assert.Contains(nameof(MapNamesViewModel.NamesCounted), names);
    }
}
