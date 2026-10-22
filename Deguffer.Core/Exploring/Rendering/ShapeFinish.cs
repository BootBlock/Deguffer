using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How every shape of one drawing is finished, in pixels: how its inside is lit, the bevel along its
/// edges, and the line round it. A <see cref="MapStyle"/> made concrete for one display, or a high
/// contrast theme's two colours (<see cref="ContrastColours"/>).
///
/// <para>Stated once and read by every route to the screen: the CPU's rasterisers, the GPU's shader
/// and the shapes a change of view carries. The colour arithmetic is whole numbers out of 256, so the
/// shader can reproduce it exactly rather than to within a rounding of it.</para>
///
/// <para>A line and a bevel are drawn inside a shape's own pixels, never across its edge, so each
/// shape still draws only what it covers and the shapes claim the canvas as they do without them. Two
/// siblings that share an edge draw a line twice as wide between them.</para>
/// </summary>
/// <param name="Shading">How the inside of each shape is lit.</param>
/// <param name="Relief">What a cushion's ridge is multiplied by: one for the standard rise.</param>
/// <param name="Bevel">The bevel along each shape's edges, where <paramref name="Shading"/> is a bevel.</param>
/// <param name="Borders">The line round each shape.</param>
public readonly record struct ShapeFinish(MapShading Shading, float Relief, ShapeBevel Bevel, ShapeBorders Borders)
{
    /// <summary>The whole of a mix: the colour mixed towards, and nothing of the one mixed from.</summary>
    public const int Whole = 256;

    private static readonly TileColour Black = new(0, 0, 0);

    private static readonly TileColour White = new(255, 255, 255);

    /// <summary>The cushions the map has always been drawn with: no bevel and no line.</summary>
    public static readonly ShapeFinish Cushioned = new(MapShading.Cushion, 1, default, default);

    /// <summary>
    /// <paramref name="style"/> on a display whose pixel at 100% is <paramref name="pixel"/> device
    /// pixels, so a line or a bevel is as heavy at 200% as at 100%.
    /// </summary>
    public static ShapeFinish Of(MapStyle style, int pixel)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixel);

        var bevel = style.Shading == MapShading.Bevel
            ? style.Relief switch
            {
                // Standard is SpaceMonger's: half way to white along the top and the left, and a
                // quarter of the way to black along the bottom and the right.
                MapRelief.Subtle => new ShapeBevel(pixel, Highlight: 64, Shadow: 32),
                MapRelief.Strong => new ShapeBevel(pixel, Highlight: 192, Shadow: 112),
                _ => new ShapeBevel(pixel, Highlight: 128, Shadow: 64),
            }
            : default;

        var blocks = Amount(style.BlockBorders);
        var folders = Amount(style.FolderBorders);
        var borders = blocks == 0 && folders == 0
            ? default
            : new ShapeBorders(
                style.BorderTone == MapBorderTone.Light ? White : Black,
                style.BorderWidth == MapBorderWidth.Thick ? pixel * 2 : pixel,
                blocks,
                folders);

        var relief = style.Relief switch
        {
            MapRelief.Subtle => 0.6f,
            MapRelief.Strong => 1.5f,
            _ => 1f,
        };

        return new ShapeFinish(style.Shading, relief, bevel, borders);
    }

    /// <summary>
    /// A high contrast theme's picture: every shape flat in the window colour, with a line of the
    /// text colour <paramref name="pixel"/> device pixels wide inside its sides, and no light.
    /// </summary>
    public static ShapeFinish Contrasted(ContrastColours colours, int pixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixel);

        return new ShapeFinish(MapShading.Flat, 1, default, new ShapeBorders(colours.Text, pixel, Whole, Whole));
    }

    /// <summary>
    /// The line round one shape filled <paramref name="fill"/>, as a change of view carries it, or null
    /// where that shape has none.
    /// </summary>
    /// <param name="folder">Whether the shape is a folder drawn round what it holds.</param>
    public QuadEdges? EdgesOf(TileColour fill, bool folder) =>
        Borders.AmountFor(folder) is > 0 and var amount
            ? new QuadEdges(Mix(fill, Borders.Tone, amount), Borders.Width)
            : null;

    /// <summary>
    /// <paramref name="colour"/> mixed <paramref name="amount"/> parts in <see cref="Whole"/> towards
    /// <paramref name="toward"/>, each channel cut to a whole level as the shader cuts it.
    /// </summary>
    public static TileColour Mix(TileColour colour, TileColour toward, int amount) => new(
        Mix(colour.Red, toward.Red, amount),
        Mix(colour.Green, toward.Green, amount),
        Mix(colour.Blue, toward.Blue, amount));

    /// <summary>The colour of a bevel's lit side, along the top and the left of a shape filled <paramref name="fill"/>.</summary>
    public TileColour Lit(TileColour fill) => Mix(fill, White, Bevel.Highlight);

    /// <summary>The colour of a bevel's shaded side, along the bottom and the right.</summary>
    public TileColour Shaded(TileColour fill) => Mix(fill, Black, Bevel.Shadow);

    /// <summary>How far a line of <paramref name="border"/> is mixed towards its tone, out of <see cref="Whole"/>.</summary>
    private static int Amount(MapBorder border) => border switch
    {
        MapBorder.Faint => 64,
        MapBorder.Medium => 128,
        MapBorder.Strong => 192,
        MapBorder.Solid => Whole,
        _ => 0,
    };

    private static byte Mix(byte channel, byte toward, int amount) =>
        (byte)(((channel * (Whole - amount)) + (toward * amount)) >> 8);
}

/// <summary>
/// A bevel along the inside of each shape's edges: <paramref name="Width"/> pixels, mixed
/// <paramref name="Highlight"/> parts in 256 towards white along the top and the left and
/// <paramref name="Shadow"/> parts towards black along the bottom and the right. No width for none.
/// </summary>
public readonly record struct ShapeBevel(int Width, int Highlight, int Shadow);

/// <summary>
/// The line inside each shape's sides: <paramref name="Width"/> pixels of the shape's own colour mixed
/// towards <paramref name="Tone"/>, by <paramref name="Blocks"/> parts in 256 for a block and by
/// <paramref name="Folders"/> for a folder drawn round what it holds. No width for none.
/// </summary>
public readonly record struct ShapeBorders(TileColour Tone, int Width, int Blocks, int Folders)
{
    /// <summary>How far the line round a shape is mixed towards the tone, or zero where none is drawn.</summary>
    /// <param name="folder">Whether the shape is a folder drawn round what it holds.</param>
    public int AmountFor(bool folder) => Width <= 0 ? 0 : folder ? Folders : Blocks;

    /// <summary>How wide the line round a shape is, or zero where none is drawn.</summary>
    public int WidthFor(bool folder) => AmountFor(folder) > 0 ? Width : 0;
}
