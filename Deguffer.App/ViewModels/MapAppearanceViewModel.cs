using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Media;
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
    private MapLook _look;

    public MapAppearanceViewModel(MapLook look) => _look = look;

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
                Apply(_look.WithScheme(View, (ExploreScheme)value));
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
                Apply(_look with { Spacing = (ExploreSpacing)value });
            }
        }
    }

    /// <summary>
    /// Take up a spacing chosen on the Settings page while the Explore page was away. Not written
    /// back, because the Settings page has already written it.
    /// </summary>
    public void Follow(ExploreSpacing spacing)
    {
        if (spacing == _look.Spacing)
        {
            return;
        }

        _look = _look with { Spacing = spacing };

        OnPropertyChanged(nameof(SpacingIndex));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(MapLook look)
    {
        if (look == _look)
        {
            return;
        }

        _look = look;

        OnPropertyChanged(nameof(SchemeIndex));
        OnPropertyChanged(nameof(SpacingIndex));
        Changed?.Invoke(this, EventArgs.Empty);

        // Applied first and written second, as the Explore page's own boxes are. A file that could not
        // be written leaves the map in the colours the reader picked, for this session.
        App.Preferences.Update(look.Into);
    }
}

/// <summary>
/// One scheme as the window offers it: a name, what it looks like in a sentence, and a strip of
/// its colours, so the choice can be made by eye before it is made on the map.
/// </summary>
/// <param name="Branches">A few branch colours at the first level, as a treemap's largest folders are drawn.</param>
/// <param name="Ages">The age bands, newest first, without the grey for an undated entry.</param>
public sealed record MapSchemeOption(
    ExploreScheme Scheme,
    string Name,
    string Description,
    IReadOnlyList<SolidColorBrush> Branches,
    IReadOnlyList<SolidColorBrush> Ages)
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

    /// <summary>Every scheme, built once for the life of the app (G5).</summary>
    public static IReadOnlyList<MapSchemeOption> All { get; } =
    [
        Option(ExploreScheme.Standard, "Standard", "Even colours. Ages run from yellow to purple."),
        Option(ExploreScheme.Vivid, "Vivid", "Stronger colours. Ages run from yellow through orange to blue."),
        Option(ExploreScheme.Soft, "Soft", "Pale colours. Ages run from pale yellow to lilac."),
        Option(ExploreScheme.Deep, "Deep", "Darker colours. Ages run from cream through red to dark purple."),
    ];

    /// <summary>What a screen reader announces for the row, which has no text beside the swatches.</summary>
    public string Spoken => $"{Name}. {Description}";

    private static MapSchemeOption Option(ExploreScheme scheme, string name, string description) =>
        new(
            scheme,
            name,
            description,
            [.. SampleHues.Select(hue => Brush(TilePalette.For(hue, 1, scheme)))],
            [.. AgePalette.Bands(scheme).SkipLast(1).Select(band => Brush(band.Colour))]);

    private static SolidColorBrush Brush(TileColour colour) =>
        new(Color.FromArgb(255, colour.Red, colour.Green, colour.Blue));
}
