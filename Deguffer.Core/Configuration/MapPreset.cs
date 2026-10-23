namespace Deguffer.Core.Configuration;

/// <summary>
/// A named look a reader can take in one choice: a <see cref="MapStyle"/> and a spacing, and for a
/// look whose colours are part of what makes it recognisable, what the colours say as well.
///
/// <para>Two of them follow other disk maps, from grounding in what those tools draw.
/// <see cref="SpaceMonger"/> is SpaceMonger 1.4's: flat boxes with a one-pixel bevel, a black line
/// round every box, each folder named in a band, and a colour per level of depth from its own eight.
/// <see cref="WizTree"/> is WizTree 4's: cushions with no line round them, a narrow gap round
/// what each folder holds, folders as dark grey frames named in their bands, and files coloured by
/// kind. Each is as close as this map draws, not a copy: the font, the background and the selection
/// stay the application's own.</para>
///
/// <para>A preset changes nothing about the schemes chosen for each view. It is applied as a whole and
/// then forgotten: a change made afterwards makes a custom look, and the window says so.</para>
/// </summary>
/// <param name="Name">What the preset is called.</param>
/// <param name="Description">What it looks like, in a sentence.</param>
/// <param name="Style">How every shape is finished.</param>
/// <param name="Spacing">How much room a treemap leaves round what each folder holds.</param>
/// <param name="Colouring">
/// What the colours on the Explore map say under this look, or null where the look leaves that to the
/// reader. Only the Explore page has a choice of colouring, so a Memory map takes the rest of the look
/// without it.
/// </param>
public sealed record MapPreset(
    string Name,
    string Description,
    MapStyle Style,
    ExploreSpacing Spacing,
    ExploreColouring? Colouring)
{
    /// <summary>The look the map is shipped with.</summary>
    public static readonly MapPreset Standard = new(
        "Deguffer",
        "Cushioned shapes coloured by branch, each folder named in a band. The look the map starts with.",
        MapStyle.Default,
        ExploreSpacing.Comfortable,
        ExploreColouring.Branch);

    /// <summary>SpaceMonger 1.4's look. See the type's remarks.</summary>
    public static readonly MapPreset SpaceMonger = new(
        "SpaceMonger",
        "Flat bevelled boxes with a black line round each, coloured by depth, as SpaceMonger draws them.",
        new MapStyle(
            Shading: MapShading.Bevel,
            Relief: MapRelief.Standard,
            BlockBorders: MapBorder.Solid,
            FolderBorders: MapBorder.Solid,
            BorderWidth: MapBorderWidth.Thin,
            BorderTone: MapBorderTone.Dark,
            FolderFill: MapFolderFill.Coloured,
            FolderNames: true,
            ShapeNames: true),
        ExploreSpacing.Comfortable,
        ExploreColouring.Depth);

    /// <summary>WizTree 4's look. See the type's remarks.</summary>
    public static readonly MapPreset WizTree = new(
        "WizTree",
        "Deep cushions with no lines, files coloured by kind inside dark grey folders, as WizTree draws them.",
        new MapStyle(
            Shading: MapShading.Cushion,
            Relief: MapRelief.Strong,
            BlockBorders: MapBorder.None,
            FolderBorders: MapBorder.None,
            BorderWidth: MapBorderWidth.Thin,
            BorderTone: MapBorderTone.Dark,
            FolderFill: MapFolderFill.Grey,
            FolderNames: true,
            ShapeNames: true),
        ExploreSpacing.Dense,
        ExploreColouring.Type);

    /// <summary>Flat colour with faint lines, and no cushion or bevel.</summary>
    public static readonly MapPreset Flat = new(
        "Flat",
        "Every shape in its own colour with no cushion or bevel, a faint line round each and a firmer one round each folder.",
        new MapStyle(
            Shading: MapShading.Flat,
            BlockBorders: MapBorder.Faint,
            FolderBorders: MapBorder.Medium),
        ExploreSpacing.Dense,
        Colouring: null);

    /// <summary>Flat colour drawn as a diagram: strong lines and no names inside the shapes.</summary>
    public static readonly MapPreset Outlined = new(
        "Outlined",
        "Flat shapes with a strong line round each and a heavy one round each folder, and only the folders named.",
        new MapStyle(
            Shading: MapShading.Flat,
            BlockBorders: MapBorder.Strong,
            FolderBorders: MapBorder.Solid,
            BorderWidth: MapBorderWidth.Thick,
            ShapeNames: false),
        ExploreSpacing.Comfortable,
        Colouring: null);

    /// <summary>Low cushions with faint pale lines and room round every folder, for a calm picture of a crowded drive.</summary>
    public static readonly MapPreset Soft = new(
        "Soft",
        "Low cushions with a faint pale line round each shape, and room round every folder.",
        new MapStyle(
            Shading: MapShading.Cushion,
            Relief: MapRelief.Subtle,
            BlockBorders: MapBorder.Faint,
            FolderBorders: MapBorder.Faint,
            BorderTone: MapBorderTone.Light),
        ExploreSpacing.Spacious,
        Colouring: null);

    /// <summary>A sheen over every shape, with folders tinted towards grey so the files carry the colour.</summary>
    public static readonly MapPreset Sheen = new(
        "Sheen",
        "Shapes lit from above, lighter along the top, inside folders tinted towards grey.",
        new MapStyle(
            Shading: MapShading.Gradient,
            Relief: MapRelief.Standard,
            BlockBorders: MapBorder.Faint,
            FolderBorders: MapBorder.Medium,
            FolderFill: MapFolderFill.Tinted),
        ExploreSpacing.Comfortable,
        Colouring: null);

    /// <summary>A deep bevel with firm dark lines, so every shape stands out from the one beside it.</summary>
    public static readonly MapPreset Embossed = new(
        "Embossed",
        "A deep bevel round every shape, a firm line round each and a strong one round each folder.",
        new MapStyle(
            Shading: MapShading.Bevel,
            Relief: MapRelief.Deep,
            BlockBorders: MapBorder.Medium,
            FolderBorders: MapBorder.Strong),
        ExploreSpacing.Comfortable,
        Colouring: null);

    /// <summary>A plan drawn in pale lines over dark grey folders, with the shapes left unnamed.</summary>
    public static readonly MapPreset Blueprint = new(
        "Blueprint",
        "Flat shapes drawn in heavy pale lines, the folders in dark grey, with only the folders named.",
        new MapStyle(
            Shading: MapShading.Flat,
            BlockBorders: MapBorder.Strong,
            FolderBorders: MapBorder.Solid,
            BorderWidth: MapBorderWidth.Heavy,
            BorderTone: MapBorderTone.Light,
            FolderFill: MapFolderFill.Grey,
            ShapeNames: false),
        ExploreSpacing.Comfortable,
        Colouring: null);

    /// <summary>Colour and nothing else: no shading, no lines, no bands and no names, packed close.</summary>
    public static readonly MapPreset Minimal = new(
        "Minimal",
        "Flat colour packed close, with no lines, no folder bands and no names.",
        new MapStyle(
            Shading: MapShading.Flat,
            FolderNames: false,
            ShapeNames: false),
        ExploreSpacing.Dense,
        Colouring: null);

    /// <summary>Every preset, in the order the appearance window lists them.</summary>
    public static IReadOnlyList<MapPreset> All { get; } =
        [Standard, SpaceMonger, WizTree, Flat, Outlined, Soft, Sheen, Embossed, Blueprint, Minimal];

    /// <summary>
    /// The preset <paramref name="look"/> is drawn as (see <see cref="Describes"/>), or null for a look
    /// of the reader's own: a preset is forgotten once applied, so this is asked of the look, never
    /// remembered beside it.
    /// </summary>
    public static MapPreset? Of(MapLook look)
    {
        ArgumentNullException.ThrowIfNull(look);

        return All.FirstOrDefault(preset => preset.Describes(look));
    }

    /// <summary><paramref name="look"/> with this preset's style and spacing, and its schemes as they were.</summary>
    public MapLook AppliedTo(MapLook look)
    {
        ArgumentNullException.ThrowIfNull(look);

        return look with { Style = Style, Spacing = Spacing };
    }

    /// <summary>
    /// <paramref name="preferences"/> with this preset's style and spacing, and nothing else changed.
    /// Not its colouring, which the Explore page applies and writes as a choice in its own Colour box.
    /// </summary>
    public AppPreferences Into(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        return preferences with { MapStyle = Style, TreemapSpacing = Spacing };
    }

    /// <summary>
    /// Whether <paramref name="look"/> is drawn as this preset draws it: the same style and spacing.
    /// The colouring is not asked, because a reader can colour any look by anything.
    /// </summary>
    public bool Describes(MapLook look)
    {
        ArgumentNullException.ThrowIfNull(look);

        return look.Style == Style && look.Spacing == Spacing;
    }
}
