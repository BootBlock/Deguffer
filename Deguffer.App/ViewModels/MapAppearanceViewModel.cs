using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the map appearance window shows and changes: the colours the picture on screen is drawn in,
/// how much room a treemap leaves round each folder, how every shape is finished, the light under the
/// pointer, and the presets that set the finish and the spacing at once.
///
/// <para>It holds the look rather than reading it from the preferences each time, and applies a
/// change before it writes it. See <see cref="MapLook"/> for why that order: somebody trying colours
/// wants each one on the map as they pick it.</para>
///
/// <para>One for the life of the application rather than one per page, because the window can open
/// before the Explore page has been built: it reopens with the application where the reader left it
/// open. The Explore page draws from this look, and tells it which view and colouring it shows.</para>
/// </summary>
public sealed partial class MapAppearanceViewModel : ObservableObject
{
    /// <summary>Every property a change of look can move, raised together so each box shows the look as it now stands.</summary>
    private static readonly string[] LookProperties =
    [
        nameof(SchemeIndex),
        nameof(SpacingIndex),
        nameof(ShadingIndex),
        nameof(ReliefIndex),
        nameof(BlockBordersIndex),
        nameof(FolderBordersIndex),
        nameof(BorderWidthIndex),
        nameof(BorderToneIndex),
        nameof(FolderFillIndex),
        nameof(FolderNames),
        nameof(ShapeNames),
        nameof(PointerLightShown),
        nameof(LightStrengthIndex),
        nameof(PresetIndex),
        nameof(PresetSummary),
    ];

    private readonly PreferenceService _preferences;

    private MapLook _look;

    /// <summary>
    /// The spacing the preferences held when this last heard from them. See <see cref="Follow"/>:
    /// it is what tells a spacing chosen on the Settings page from one this window chose and could
    /// not write.
    /// </summary>
    private ExploreSpacing _storedSpacing;

    /// <param name="preferences">
    /// Where the look, the view and the colouring are read from, once, and where a change is written,
    /// one setting at a time. The look is not read again, because re-reading would undo a choice whose
    /// write failed. Only a spacing chosen on the Settings page is followed (see <see cref="Follow"/>).
    /// </param>
    public MapAppearanceViewModel(PreferenceService preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var current = preferences.Current;
        var choice = ExploreChoice.From(current);

        _look = MapLook.From(current);
        _storedSpacing = _look.Spacing;
        _preferences = preferences;

        View = choice.View;
        Colouring = choice.Colouring;

        // Heard for as long as the preferences last, which is as long as this does.
        preferences.Changed += (_, _) => Follow(preferences.Current.TreemapSpacing);
    }

    /// <summary>The look changed. Whoever draws a map draws it again.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A preset whose look is part of what its colours say was chosen, and the Explore page is to
    /// colour its map by this and move its Colour box to it. Raised before the colouring is written,
    /// and the colouring is written whether or not the page has been built yet, so it is what the page
    /// opens with.
    /// </summary>
    public event EventHandler<ExploreColouring>? ColouringChosen;

    /// <summary>The look as it stands, whether or not it has reached the preferences file yet.</summary>
    public MapLook Look => _look;

    /// <summary>Every scheme, in the order the window lists them.</summary>
    public IReadOnlyList<MapSchemeOption> Schemes => MapSchemeOption.All;

    /// <summary>Every preset, in the order the window lists them.</summary>
    public IReadOnlyList<MapPresetOption> Presets => MapPresetOption.All;

    /// <summary>
    /// The view the Explore page is showing, which is the one the scheme picker speaks for. Set by the
    /// page whenever its View box moves, and the stored view until the page is built.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchemeHeading))]
    [NotifyPropertyChangedFor(nameof(SchemeIndex))]
    [NotifyPropertyChangedFor(nameof(PreviewCaption))]
    public partial ExploreView View { get; set; }

    /// <summary>
    /// What the colours on the Explore page's map say, which the preview is coloured by as near as it
    /// can be. Set by the page whenever its Colour box moves, and by a preset with a colouring of its own.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewCaption))]
    public partial ExploreColouring Colouring { get; set; }

    /// <summary>Which picture the scheme picker changes, said in words because the picker is one list for three views.</summary>
    public string SchemeHeading => MapLook.Drawn(View) switch
    {
        ExploreView.Icicle => "Colours for the icicle",
        ExploreView.Sunburst => "Colours for the sunburst",
        _ => "Colours for the treemap",
    };

    /// <summary>What the preview shows, in words: the picture it draws and what its colours say.</summary>
    public string PreviewCaption => MapLook.Drawn(View) switch
    {
        ExploreView.Icicle => $"Icicle, {ColouredBy(Colouring)}",
        ExploreView.Sunburst => $"Sunburst, {ColouredBy(Colouring)}",
        _ => $"Treemap, {ColouredBy(Colouring)}",
    };

    /// <summary>Index into the scheme list, ordered to match <see cref="ExploreScheme"/>.</summary>
    public int SchemeIndex
    {
        get => (int)_look.SchemeFor(View);
        set
        {
            // A list with nothing selected reports -1 while it rebuilds, which is not a choice.
            if (value >= 0)
            {
                var view = View;
                var scheme = (ExploreScheme)value;

                Apply(
                    _look.WithScheme(view, scheme),
                    current => MapLook.From(current).WithScheme(view, scheme).Into(current));
            }
        }
    }

    /// <summary>Index into the spacing list, ordered to match <see cref="ExploreSpacing"/>.</summary>
    public int SpacingIndex
    {
        get => (int)_look.Spacing;
        set
        {
            if (value >= 0)
            {
                var spacing = (ExploreSpacing)value;

                if (Apply(_look with { Spacing = spacing }, current => current with { TreemapSpacing = spacing }))
                {
                    _storedSpacing = spacing;
                }
            }
        }
    }

    /// <summary>Index into the shading list, ordered to match <see cref="MapShading"/>.</summary>
    public int ShadingIndex
    {
        get => (int)_look.Style.Shading;
        set => Restyle(value, style => style with { Shading = (MapShading)value });
    }

    /// <summary>Index into the relief list, ordered to match <see cref="MapRelief"/>.</summary>
    public int ReliefIndex
    {
        get => (int)_look.Style.Relief;
        set => Restyle(value, style => style with { Relief = (MapRelief)value });
    }

    /// <summary>Index into the list of lines round each block, ordered to match <see cref="MapBorder"/>.</summary>
    public int BlockBordersIndex
    {
        get => (int)_look.Style.BlockBorders;
        set => Restyle(value, style => style with { BlockBorders = (MapBorder)value });
    }

    /// <summary>Index into the list of lines round each folder, ordered to match <see cref="MapBorder"/>.</summary>
    public int FolderBordersIndex
    {
        get => (int)_look.Style.FolderBorders;
        set => Restyle(value, style => style with { FolderBorders = (MapBorder)value });
    }

    /// <summary>Index into the line width list, ordered to match <see cref="MapBorderWidth"/>.</summary>
    public int BorderWidthIndex
    {
        get => (int)_look.Style.BorderWidth;
        set => Restyle(value, style => style with { BorderWidth = (MapBorderWidth)value });
    }

    /// <summary>Index into the line tone list, ordered to match <see cref="MapBorderTone"/>.</summary>
    public int BorderToneIndex
    {
        get => (int)_look.Style.BorderTone;
        set => Restyle(value, style => style with { BorderTone = (MapBorderTone)value });
    }

    /// <summary>Index into the folder fill list, ordered to match <see cref="MapFolderFill"/>.</summary>
    public int FolderFillIndex
    {
        get => (int)_look.Style.FolderFill;
        set => Restyle(value, style => style with { FolderFill = (MapFolderFill)value });
    }

    /// <summary>Whether each treemap folder with room for one keeps a band with its name in it.</summary>
    public bool FolderNames
    {
        get => _look.Style.FolderNames;
        set => Restyle(style => style with { FolderNames = value });
    }

    /// <summary>Whether a shape with room for it has its name and size written inside it.</summary>
    public bool ShapeNames
    {
        get => _look.Style.ShapeNames;
        set => Restyle(style => style with { ShapeNames = value });
    }

    /// <summary>Whether the map shines a soft light over the point under the pointer.</summary>
    public bool PointerLightShown
    {
        get => _look.Light.Shown;
        set => Relight(light => light with { Shown = value });
    }

    /// <summary>Index into the light's strength list, ordered to match <see cref="MapLightStrength"/>.</summary>
    public int LightStrengthIndex
    {
        get => (int)_look.Light.Strength;
        set
        {
            if (value >= 0)
            {
                Relight(light => light with { Strength = (MapLightStrength)value });
            }
        }
    }

    /// <summary>
    /// Index into the preset list of the preset the look is drawn as, or -1 for a look of the reader's
    /// own. Set to apply that preset.
    /// </summary>
    public int PresetIndex
    {
        get => MapPreset.Of(_look) is { } preset
            ? MapPresetOption.All.Index().First(entry => entry.Item.Preset == preset).Index
            : -1;
        set
        {
            if (value >= 0 && value < MapPresetOption.All.Count)
            {
                Choose(MapPresetOption.All[value].Preset);
            }
        }
    }

    /// <summary>Which preset the look is drawn as, in words, or that it is the reader's own.</summary>
    public string PresetSummary => MapPreset.Of(_look) is { } preset
        ? $"The map is drawn as the {preset.Name} preset."
        : "The map is drawn in a look of your own. Choose a preset to start again from it.";

    /// <summary>What a map coloured by <paramref name="colouring"/> shows, in a few words.</summary>
    public static string ColouredBy(ExploreColouring colouring) => colouring switch
    {
        ExploreColouring.Age => "coloured by age",
        ExploreColouring.Growth => "coloured by growth",
        ExploreColouring.Type => "coloured by type",
        ExploreColouring.Depth => "coloured by depth",
        _ => "coloured by branch",
    };

    /// <summary>
    /// Put every part of the look back as the map is shipped: the Deguffer preset, each picture's
    /// standard colours and the light as it always was, with the preset's colouring handed to the page
    /// as choosing the preset hands it.
    /// </summary>
    public void RestoreDefaults()
    {
        if (Apply(MapLook.Default, MapLook.Default.Into))
        {
            _storedSpacing = MapLook.Default.Spacing;
        }

        HandOver(MapPreset.Standard.Colouring);
    }

    /// <summary>
    /// Take up a spacing chosen on the Settings page, which writes it while this window can be open.
    /// Heard on every change to the preferences, and not written back, because the Settings page has
    /// already written it.
    ///
    /// <para>Only a spacing that moved in the preferences since this last heard from them. One this
    /// window chose and could not write leaves the preferences where they were, and following them
    /// back on the next change to any other setting would undo the reader's choice.</para>
    /// </summary>
    private void Follow(ExploreSpacing stored)
    {
        if (stored == _storedSpacing)
        {
            return;
        }

        _storedSpacing = stored;

        if (stored == _look.Spacing)
        {
            return;
        }

        _look = _look with { Spacing = stored };

        Raise();
    }

    /// <summary>
    /// Show and write <paramref name="preset"/>'s look in one change, and hand its colouring to the
    /// page where it has one. The colouring goes even where the look was already the preset's, because
    /// a reader choosing it again is asking for the whole of it.
    /// </summary>
    private void Choose(MapPreset preset)
    {
        if (Apply(preset.AppliedTo(_look), preset.Into))
        {
            _storedSpacing = preset.Spacing;
        }

        HandOver(preset.Colouring);
    }

    /// <summary>
    /// Colour the map by <paramref name="colouring"/> where there is one: shown in the preview and on
    /// the page first, then written, in the order every other choice here takes.
    /// </summary>
    private void HandOver(ExploreColouring? colouring)
    {
        if (colouring is not { } chosen)
        {
            return;
        }

        Colouring = chosen;
        ColouringChosen?.Invoke(this, chosen);

        _preferences.Update(current => current with { ExploreColours = chosen });
    }

    /// <summary>Change one part of the style, where <paramref name="index"/> is a choice. See <see cref="SchemeIndex"/> for -1.</summary>
    private void Restyle(int index, Func<MapStyle, MapStyle> change)
    {
        if (index >= 0)
        {
            Restyle(change);
        }
    }

    /// <summary>
    /// Change one part of the style, on screen and in the preferences. Only that part is written: the
    /// rest of the stored style is left as the file holds it.
    /// </summary>
    private void Restyle(Func<MapStyle, MapStyle> change) =>
        Apply(
            _look with { Style = change(_look.Style) },
            current => current with { MapStyle = change(current.MapStyle.Defined()) });

    /// <summary>Change one part of the pointer's light, on screen and in the preferences, as <see cref="Restyle(Func{MapStyle, MapStyle})"/> changes the style.</summary>
    private void Relight(Func<MapPointerLight, MapPointerLight> change) =>
        Apply(
            _look with { Light = change(_look.Light) },
            current => current with { MapPointerLight = change(current.MapPointerLight.Defined()) });

    /// <summary>
    /// Show <paramref name="look"/>, then write the settings that changed through
    /// <paramref name="write"/>.
    ///
    /// <para>Applied first and written second, as the Explore page's own boxes are. A file that could
    /// not be written leaves the map in the look the reader picked, for this session.</para>
    ///
    /// <para>Only what changed rather than the whole look. The spacing is also chosen on the Settings
    /// page while this window can stay open, and writing every field would put back whatever this
    /// window last knew over what the reader has since chosen there.</para>
    /// </summary>
    /// <returns>Whether the change reached the preferences file.</returns>
    private bool Apply(MapLook look, Func<AppPreferences, AppPreferences> write)
    {
        if (look == _look)
        {
            return true;
        }

        _look = look;

        Raise();

        return _preferences.Update(write);
    }

    /// <summary>Tell every box and whoever draws a map that the look moved.</summary>
    private void Raise()
    {
        foreach (var property in LookProperties)
        {
            OnPropertyChanged(property);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
