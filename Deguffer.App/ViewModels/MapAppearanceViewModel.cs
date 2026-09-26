using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Windows.UI;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the map appearance window shows and changes: the colours the picture on screen is drawn in,
/// and how much room a treemap leaves round each folder.
///
/// <para>It holds the look rather than reading it from the preferences each time, and applies a
/// change before it writes it. See <see cref="MapLook"/> for why that order: somebody trying colours
/// wants each one on the map as they pick it.</para>
/// </summary>
public sealed partial class MapAppearanceViewModel : ObservableObject
{
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

    /// <summary>The look as it stands, whether or not it has reached the preferences file yet.</summary>
    public MapLook Look => _look;

    /// <summary>Every scheme, in the order the window lists them.</summary>
    public IReadOnlyList<MapSchemeOption> Schemes => MapSchemeOption.All;

    /// <summary>
    /// The view the page is showing, which is the one the scheme picker speaks for. Set by the page
    /// whenever its View box moves.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchemeHeading))]
    [NotifyPropertyChangedFor(nameof(SchemeIndex))]
    public partial ExploreView View { get; set; }

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

        OnPropertyChanged(nameof(SpacingIndex));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Show <paramref name="look"/>, then write the one setting that changed through
    /// <paramref name="write"/>.
    ///
    /// <para>Applied first and written second, as the Explore page's own boxes are. A file that could
    /// not be written leaves the map in the colours the reader picked, for this session.</para>
    ///
    /// <para>The one setting rather than the whole look. The spacing is also chosen on the Settings
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

        OnPropertyChanged(nameof(SchemeIndex));
        OnPropertyChanged(nameof(SpacingIndex));
        Changed?.Invoke(this, EventArgs.Empty);

        return _preferences.Update(write);
    }
}

/// <summary>
/// One scheme as the window offers it: a name, what it looks like in a sentence, and a strip of
/// its colours, so the choice can be made by eye before it is made on the map.
///
/// <para>Which scheme an option is, is its place in <see cref="All"/>: the list is bound by index,
/// as the other boxes indexed against an enum are, so <see cref="All"/> is in
/// <see cref="ExploreScheme"/>'s order and built from it.</para>
///
/// <para>The strips are colours rather than brushes. A brush is a XAML object, and one made in a
/// static initialiser ties every reader of <see cref="All"/> to a running XAML host; the window's
/// template paints each swatch from its colour instead. The strips are decoration: the name and the
/// sentence say what the scheme is, with or without them (§6.5).</para>
/// </summary>
/// <param name="Branches">A few branch colours at the first level, as a treemap's largest folders are drawn.</param>
/// <param name="Ages">The age bands, newest first, without the grey for an undated entry.</param>
public sealed record MapSchemeOption(
    string Name,
    string Description,
    IReadOnlyList<MapSwatch> Branches,
    IReadOnlyList<MapSwatch> Ages)
{
    /// <summary>
    /// Hues spread round the circle, and alternately lifted, as a folder's children are given them.
    /// </summary>
    private static readonly BranchHue[] SampleHues =
    [
        new(0, 30, false),
        new(60, 30, true),
        new(120, 30, false),
        new(180, 30, true),
        new(240, 30, false),
        new(300, 30, true),
    ];

    /// <summary>Every scheme, in <see cref="ExploreScheme"/>'s order, built once for the life of the app (G5).</summary>
    public static IReadOnlyList<MapSchemeOption> All { get; } =
        [.. Enum.GetValues<ExploreScheme>().Select(Option)];

    /// <summary>What a screen reader announces for the row, which has no text beside the swatches.</summary>
    public string Spoken => $"{Name}. {Description}";

    private static MapSchemeOption Option(ExploreScheme scheme)
    {
        var (name, description) = scheme switch
        {
            ExploreScheme.Vivid => ("Vivid", "Stronger colours. Ages run from yellow through orange to blue."),
            ExploreScheme.Soft => ("Soft", "Pale colours. Ages run from pale yellow to lilac."),
            ExploreScheme.Deep => ("Deep", "Darker colours. Ages run from cream through red to dark purple."),
            _ => ("Standard", "Even colours. Ages run from yellow to purple."),
        };

        return new(
            name,
            description,
            [.. SampleHues.Select(hue => new MapSwatch(TilePalette.For(hue, 1, scheme)))],
            [.. AgePalette.Bands(scheme).SkipLast(1).Select(band => new MapSwatch(band.Colour))]);
    }
}

/// <summary>One colour of a scheme's strip, as the map draws it.</summary>
/// <param name="Tile">The colour as the map's palette states it.</param>
public sealed record MapSwatch(TileColour Tile)
{
    /// <summary>The colour the window's template paints the swatch in. Opaque, as a tile is drawn.</summary>
    public Color Colour => Color.FromArgb(255, Tile.Red, Tile.Green, Tile.Blue);
}
