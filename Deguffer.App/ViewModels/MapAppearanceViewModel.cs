using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the map appearance window shows and changes: the colours the picture on screen is drawn in,
/// how much room a treemap leaves round each folder, how every shape is finished, and the presets
/// that set all of those at once.
///
/// <para>It holds the look rather than reading it from the preferences each time, and applies a
/// change before it writes it. See <see cref="MapLook"/> for why that order: somebody trying colours
/// wants each one on the map as they pick it.</para>
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

    /// <param name="look">
    /// The look the page is drawing, read from <paramref name="preferences"/> once, when the page was
    /// built. Not read again here, for the reason the page reads it once: re-reading would undo a
    /// choice whose write failed.
    /// </param>
    /// <param name="preferences">Where a change is written, one setting at a time.</param>
    public MapAppearanceViewModel(MapLook look, PreferenceService preferences)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(preferences);

        _look = look;
        _storedSpacing = look.Spacing;
        _preferences = preferences;
    }

    /// <summary>The look changed. Whoever draws a map draws it again.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A preset whose look is part of what its colours say was chosen, and the page is to colour its
    /// map by this. The page applies and writes it as it does a choice in its own Colour box, which is
    /// where that choice belongs.
    /// </summary>
    public event EventHandler<ExploreColouring>? ColouringChosen;

    /// <summary>The look as it stands, whether or not it has reached the preferences file yet.</summary>
    public MapLook Look => _look;

    /// <summary>Every scheme, in the order the window lists them.</summary>
    public IReadOnlyList<MapSchemeOption> Schemes => MapSchemeOption.All;

    /// <summary>Every preset, in the order the window lists them.</summary>
    public IReadOnlyList<MapPreset> Presets => MapPreset.All;

    /// <summary>
    /// The view the page is showing, which is the one the scheme picker speaks for. Set by the page
    /// whenever its View box moves.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchemeHeading))]
    [NotifyPropertyChangedFor(nameof(SchemeIndex))]
    public partial ExploreView View { get; set; }

    /// <summary>
    /// What the colours on the page's map say, which the preview is coloured by as near as it can be.
    /// Set by the page whenever its Colour box moves.
    /// </summary>
    [ObservableProperty]
    public partial ExploreColouring Colouring { get; set; }

    /// <summary>Which picture the scheme picker changes, said in words because the picker is one list for three views.</summary>
    public string SchemeHeading => MapLook.Drawn(View) switch
    {
        ExploreView.Icicle => "Colours for the icicle",
        ExploreView.Sunburst => "Colours for the sunburst",
        _ => "Colours for the treemap",
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

    /// <summary>
    /// Index into the preset list of the preset the look is drawn as, or -1 for a look of the reader's
    /// own. Set to apply that preset.
    /// </summary>
    public int PresetIndex
    {
        get => IndexOfPreset();
        set
        {
            if (value >= 0 && value < MapPreset.All.Count)
            {
                Choose(MapPreset.All[value]);
            }
        }
    }

    /// <summary>Which preset the look is drawn as, in words, or that it is the reader's own.</summary>
    public string PresetSummary => IndexOfPreset() is var index and >= 0
        ? $"The map is drawn as the {MapPreset.All[index].Name} preset."
        : "The map is drawn in a look of your own. Choose a preset to start again from it.";

    /// <summary>
    /// Take up a spacing chosen on the Settings page while the Explore page was away. Not written
    /// back, because the Settings page has already written it.
    ///
    /// <para>Only a spacing that moved in the preferences since this last heard from them. One this
    /// window chose and could not write leaves the preferences where they were, and following them
    /// back would undo the reader's choice on the next visit.</para>
    /// </summary>
    public void Follow(ExploreSpacing stored)
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

    private int IndexOfPreset()
    {
        for (var i = 0; i < MapPreset.All.Count; i++)
        {
            if (MapPreset.All[i].Describes(_look))
            {
                return i;
            }
        }

        return -1;
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

        if (preset.Colouring is { } colouring)
        {
            ColouringChosen?.Invoke(this, colouring);
        }
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
