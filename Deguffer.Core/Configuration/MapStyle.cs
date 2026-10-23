namespace Deguffer.Core.Configuration;

/// <summary>
/// How the shapes of a map are finished: how each is shaded, how prominent the lines round it are,
/// what a folder drawn round what it holds is filled with, and what text is written on the picture.
/// Every map draws in it, the Explore page's and the Memory page's alike.
///
/// <para>Presentation only, as every other part of the map's look is: it changes how a shape is
/// drawn, never which shapes there are, what they stand for, or what may be removed.</para>
///
/// <para>A group of its own in the preferences, as <see cref="AppPreferences.Scanning"/> is, so a
/// file written before it existed reads with every value at the look the map always had.</para>
/// </summary>
/// <param name="Shading">How the inside of each shape is lit.</param>
/// <param name="Relief">
/// How strongly a cushion rises, a bevel stands out or a sheen runs from light to dark. Nothing for a
/// flat shape.
/// </param>
/// <param name="BlockBorders">
/// How prominent the line round each block is: a file, a folder drawn as one block, and every shape
/// of an icicle or a sunburst, whose folders sit beside what they hold rather than round it.
/// </param>
/// <param name="FolderBorders">
/// How prominent the line round a treemap folder drawn round what it holds is. Apart from
/// <paramref name="BlockBorders"/>, so the frames of the hierarchy can be stronger or fainter than
/// the files inside them.
/// </param>
/// <param name="BorderWidth">How wide either line is.</param>
/// <param name="BorderTone">What a line is mixed towards: black, or white.</param>
/// <param name="FolderFill">What a treemap folder drawn round what it holds is filled with.</param>
/// <param name="FolderNames">
/// Whether a treemap folder with room for it keeps a band along its top with its name in it. Without
/// the bands the folders give that room to what they hold.
/// </param>
/// <param name="ShapeNames">Whether a shape with room for it has its name and size written inside it.</param>
public sealed record MapStyle(
    MapShading Shading = MapShading.Cushion,
    MapRelief Relief = MapRelief.Standard,
    MapBorder BlockBorders = MapBorder.None,
    MapBorder FolderBorders = MapBorder.None,
    MapBorderWidth BorderWidth = MapBorderWidth.Thin,
    MapBorderTone BorderTone = MapBorderTone.Dark,
    MapFolderFill FolderFill = MapFolderFill.Coloured,
    bool FolderNames = true,
    bool ShapeNames = true)
{
    /// <summary>The look the map had before any of this could be chosen.</summary>
    public static readonly MapStyle Default = new();

    /// <summary>
    /// This style with every value no member names read as the default's.
    ///
    /// <para>The preferences file is read with numbers allowed for an enum, so a hand-edited
    /// <c>"Shading": 9</c> arrives as a value no drawing knows. A stray value in a cosmetic setting
    /// must never stop the map drawing (see <see cref="PreferenceStore.Load"/>).</para>
    /// </summary>
    public MapStyle Defined() => new(
        StoredEnum.Defined(Shading, Default.Shading),
        StoredEnum.Defined(Relief, Default.Relief),
        StoredEnum.Defined(BlockBorders, Default.BlockBorders),
        StoredEnum.Defined(FolderBorders, Default.FolderBorders),
        StoredEnum.Defined(BorderWidth, Default.BorderWidth),
        StoredEnum.Defined(BorderTone, Default.BorderTone),
        StoredEnum.Defined(FolderFill, Default.FolderFill),
        FolderNames,
        ShapeNames);
}

/// <summary>
/// How the inside of each shape is lit. The values are ordinal, and the appearance window lists them
/// in this order. They are stored by name, as <see cref="ExploreView"/> is.
/// </summary>
public enum MapShading
{
    /// <summary>
    /// A cushion: each shape rises to a ridge and falls away to its edges, lit from above and to the
    /// left, so nesting reads without a line round every shape.
    /// </summary>
    Cushion = 0,

    /// <summary>
    /// Flat inside, with a narrow bevel along its edges: lighter along the top and the left, darker
    /// along the bottom and the right, as a raised button is drawn.
    /// </summary>
    Bevel = 1,

    /// <summary>Each shape in its own colour and nothing else: no cushion and no bevel. The pointer's light still falls on it.</summary>
    Flat = 2,

    /// <summary>
    /// Flat across, with a soft sheen from top to bottom: lighter along its top and darker along its
    /// bottom, as though lit from above, with none of a cushion's rise to the middle. A sunburst's runs
    /// along the radius instead, lighter at a ring's inner edge than at its outer.
    /// </summary>
    Gradient = 3,
}

/// <summary>
/// How strongly a cushion rises, a bevel stands out or a sheen runs from light to dark. The values are
/// ordinal, and the appearance window lists them in this order.
/// </summary>
public enum MapRelief
{
    Subtle = 0,
    Standard = 1,
    Strong = 2,

    /// <summary>Further than strong: steep cushions, a sheen from near white to near shadow, and a bevel cut deep.</summary>
    Deep = 3,
}

/// <summary>
/// How prominent a line round a shape is: how far its colour is mixed from the shape's own towards
/// the <see cref="MapBorderTone"/>, from not drawn at all to the tone itself. The values are ordinal,
/// and the appearance window lists them in this order.
/// </summary>
public enum MapBorder
{
    None = 0,
    Faint = 1,
    Medium = 2,
    Strong = 3,
    Solid = 4,
}

/// <summary>How wide a line round a shape is, at the display's scale. Ordinal, as the others are.</summary>
public enum MapBorderWidth
{
    /// <summary>One pixel at 100%.</summary>
    Thin = 0,

    /// <summary>Two pixels at 100%.</summary>
    Thick = 1,

    /// <summary>Three pixels at 100%, for a look drawn as a diagram rather than a picture.</summary>
    Heavy = 2,
}

/// <summary>What a line round a shape is mixed towards. Ordinal, as the others are.</summary>
public enum MapBorderTone
{
    Dark = 0,
    Light = 1,
}

/// <summary>
/// What a treemap folder drawn round what it holds is filled with: the band along its top and the
/// gap round its contents. Ordinal, as the others are.
/// </summary>
public enum MapFolderFill
{
    /// <summary>The colour the map's colouring gives the folder.</summary>
    Coloured = 0,

    /// <summary>
    /// One neutral grey for every folder, so the colours on the map are the files' alone and the
    /// folders read as the containers round them.
    /// </summary>
    Grey = 1,

    /// <summary>
    /// The folder's own colour mixed half way towards the neutral grey, so the folders recede behind
    /// what they hold, as grey ones do, and still say which branch each is.
    /// </summary>
    Tinted = 2,
}
