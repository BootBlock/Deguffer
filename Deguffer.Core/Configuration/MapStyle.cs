using System.Text.Json.Serialization;

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
/// <param name="Lines">
/// Whether two shapes side by side each draw their own line, or share one between them.
/// </param>
/// <param name="Lettering">How the text on the picture is set, and what it says.</param>
/// <param name="BevelShades">What shades a bevel's two sides are drawn in.</param>
/// <param name="BevelWidth">How wide a bevel's two sides are.</param>
/// <param name="Saturation">
/// How strong the colours the map's colouring gives each shape are, from none, which draws the map
/// in greys, to stronger than the palette's own.
/// </param>
/// <param name="Brightness">How far each shape's colour is lightened or darkened from the palette's own.</param>
public sealed record MapStyle(
    MapShading Shading = MapShading.Cushion,
    MapRelief Relief = MapRelief.Standard,
    MapBorder BlockBorders = MapBorder.None,
    MapBorder FolderBorders = MapBorder.None,
    MapBorderWidth BorderWidth = MapBorderWidth.Thin,
    MapBorderTone BorderTone = MapBorderTone.Dark,
    MapFolderFill FolderFill = MapFolderFill.Coloured,
    bool FolderNames = true,
    bool ShapeNames = true,
    MapLines Lines = MapLines.Own,
    MapLettering Lettering = MapLettering.Standard,
    MapBevelShades BevelShades = MapBevelShades.Mixed,
    MapBevelWidth BevelWidth = MapBevelWidth.Narrow,
    MapSaturation Saturation = MapSaturation.Standard,
    MapBrightness Brightness = MapBrightness.Standard)
{
    /// <summary>The look the map had before any of this could be chosen.</summary>
    public static readonly MapStyle Default = new();

    /// <summary>
    /// Whether the shapes rise at all, so <see cref="Relief"/> changes anything: a flat shape is its
    /// own colour whatever the relief. Kept out of the file, as every value derived from the others is.
    /// </summary>
    [JsonIgnore]
    public bool Raised => Shading != MapShading.Flat;

    /// <summary>Whether the shapes are bevelled, so <see cref="BevelShades"/> and <see cref="BevelWidth"/> change anything.</summary>
    [JsonIgnore]
    public bool Bevelled => Shading == MapShading.Bevel;

    /// <summary>
    /// Whether any line is drawn round a shape, so <see cref="BorderWidth"/>, <see cref="BorderTone"/>
    /// and <see cref="Lines"/> change anything.
    /// </summary>
    [JsonIgnore]
    public bool Outlined => BlockBorders != MapBorder.None || FolderBorders != MapBorder.None;

    /// <summary>Whether any name is written on the picture, so what a name says changes anything.</summary>
    [JsonIgnore]
    public bool Named => FolderNames || ShapeNames;

    /// <summary>
    /// Whether the names are set in the application's own lettering, whose size and number the reader
    /// can choose (<see cref="MapDetail.TextSize"/>, <see cref="MapDetail.Names"/>). SpaceMonger's is set
    /// at its own size and names every box with room.
    /// </summary>
    [JsonIgnore]
    public bool OwnLettering => Named && Lettering == MapLettering.Standard;

    /// <summary>
    /// Whether shapes are named in the application's own lettering, so how many are named
    /// (<see cref="MapDetail.Names"/>) changes anything. A folder's band is named whatever that says,
    /// because an empty band reads as a folder with no name.
    /// </summary>
    [JsonIgnore]
    public bool NamesCounted => ShapeNames && Lettering == MapLettering.Standard;

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
        ShapeNames,
        StoredEnum.Defined(Lines, Default.Lines),
        StoredEnum.Defined(Lettering, Default.Lettering),
        StoredEnum.Defined(BevelShades, Default.BevelShades),
        StoredEnum.Defined(BevelWidth, Default.BevelWidth),
        StoredEnum.Defined(Saturation, Default.Saturation),
        StoredEnum.Defined(Brightness, Default.Brightness));
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

    /// <summary>Further than strong: steep cushions, a sheen about a third lighter at its lit edge and a third darker at the far one, and a bevel cut deep.</summary>
    Deep = 3,
}

/// <summary>
/// What shades a bevel's two sides are drawn in. Ordinal, as the others are.
/// </summary>
public enum MapBevelShades
{
    /// <summary>The shape's colour mixed towards white and towards black, as far as the relief says.</summary>
    Mixed = 0,

    /// <summary>
    /// SpaceMonger's: for each of the eight colours it draws its levels in, the lighter and the darker
    /// shade it draws that colour's edges in, and the mix for any other colour.
    /// </summary>
    Classic = 1,
}

/// <summary>How wide a bevel's two sides are, at the display's scale. Ordinal, as the others are.</summary>
public enum MapBevelWidth
{
    /// <summary>One pixel at 100%, as SpaceMonger draws it.</summary>
    Narrow = 0,

    /// <summary>Two pixels at 100%.</summary>
    Medium = 1,

    /// <summary>Three pixels at 100%, for a bevel that reads as a raised tile.</summary>
    Wide = 2,
}

/// <summary>
/// How strong each shape's colour is, as a multiple of the palette's own. Ordinal, as the others are.
/// Never the colour of a block, such as free space, whose grey says what it is.
/// </summary>
public enum MapSaturation
{
    /// <summary>
    /// No colour at all: each shape in the grey of its colour's luma (<see cref="Exploring.Rendering.ColourTuning"/>),
    /// so only the shading, the lines and how light each shape is tell the shapes apart.
    /// </summary>
    Grey = 0,

    /// <summary>A little under half the palette's colour.</summary>
    Muted = 1,

    /// <summary>Three quarters of the palette's colour.</summary>
    Soft = 2,

    /// <summary>The palette's colour, as the map has always drawn it.</summary>
    Standard = 3,

    /// <summary>A third more than the palette's colour, as far as a screen can show it.</summary>
    Vivid = 4,
}

/// <summary>
/// How far each shape's colour is mixed towards black or towards white. Ordinal, as the others are.
/// Never the colour of a block, as for <see cref="MapSaturation"/>.
/// </summary>
public enum MapBrightness
{
    /// <summary>Two fifths of the way to black.</summary>
    MuchDarker = 0,

    /// <summary>A fifth of the way to black.</summary>
    Darker = 1,

    /// <summary>The palette's own colour.</summary>
    Standard = 2,

    /// <summary>A fifth of the way to white.</summary>
    Lighter = 3,

    /// <summary>Two fifths of the way to white.</summary>
    MuchLighter = 4,
}

/// <summary>
/// Whether two shapes side by side each draw their own line, or share one. Ordinal, as the others are.
/// </summary>
public enum MapLines
{
    /// <summary>
    /// Each shape draws its line inside all four of its sides, so two side by side have a line twice
    /// as wide between them, and each shape is outlined whole.
    /// </summary>
    Own = 0,

    /// <summary>
    /// Each shape draws its line along its top and its left, and one past its bottom and its right, so
    /// two shapes side by side share one line and what a folder holds is closed by one line round it,
    /// as SpaceMonger draws them.
    /// </summary>
    Shared = 1,
}

/// <summary>How the text on the picture is set, and what it says. Ordinal, as the others are.</summary>
public enum MapLettering
{
    /// <summary>
    /// The application's own type, with each name followed by its size, at the top left of its shape
    /// or along its folder's band, and cut short with an ellipsis where it does not fit.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// SpaceMonger's: small Microsoft Sans Serif type drawn without smoothing, a folder named in its band with no size, and a
    /// file's name, its size in bytes and the date it was last written, as many of those lines as its
    /// box has room for, in the middle of the box and cut off at its edge.
    /// </summary>
    Classic = 1,
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
