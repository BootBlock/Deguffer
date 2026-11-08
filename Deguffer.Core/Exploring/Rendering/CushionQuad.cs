using System.Runtime.InteropServices;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One rectangle of a treemap as it is shaded: the pixels it covers, how far its cushion rises, and
/// its colour. The one place a laid-out shape becomes something to shade, so the CPU and the GPU shade
/// the same shapes from the same edges (see <see cref="CushionQuads"/>).
///
/// <para>Laid out field by field, because the GPU reads an array of these as it stands: four edges as
/// integers, the ridge as a float, the colour's three bytes and a byte saying whether the quad is a
/// folder drawn round what it holds, which the GPU reads as the colour's fourth, and then the bevel's
/// two colours, each read as four bytes of which the fourth means nothing. Fields rather than
/// properties, so the renderer takes the size and the offsets it hands the GPU from this type, by
/// name, rather than restating them.</para>
/// </summary>
/// <param name="Left">The first column covered.</param>
/// <param name="Top">The first row covered.</param>
/// <param name="Right">The column after the last covered.</param>
/// <param name="Bottom">The row after the last covered.</param>
/// <param name="Ridge">How far the surface tilts at the edges: see <see cref="CushionShading.RidgeAt"/>. Zero for a flat shape.</param>
/// <param name="Colour">The colour the light is a fraction of.</param>
/// <param name="Framed">
/// Whether the quad is a folder drawn round what it holds, whose line is a folder's
/// (<see cref="ShapeBorders.Folders"/>) rather than a block's.
/// </param>
/// <param name="Lit">The colour of its bevel's lit side, where it has a bevel (<see cref="ShapeFinish.Lit"/>).</param>
/// <param name="Shaded">The colour of its bevel's shaded side (<see cref="ShapeFinish.Shaded"/>).</param>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly record struct CushionQuad(
    int Left,
    int Top,
    int Right,
    int Bottom,
    float Ridge,
    TileColour Colour,
    bool Framed = false,
    TileColour Lit = default,
    TileColour Shaded = default)
{
    [FieldOffset(0)]
    public readonly int Left = Left;

    [FieldOffset(4)]
    public readonly int Top = Top;

    [FieldOffset(8)]
    public readonly int Right = Right;

    [FieldOffset(12)]
    public readonly int Bottom = Bottom;

    [FieldOffset(16)]
    public readonly float Ridge = Ridge;

    [FieldOffset(20)]
    public readonly TileColour Colour = Colour;

    [FieldOffset(23)]
    [MarshalAs(UnmanagedType.U1)]
    public readonly bool Framed = Framed;

    [FieldOffset(24)]
    public readonly TileColour Lit = Lit;

    [FieldOffset(28)]
    public readonly TileColour Shaded = Shaded;

    /// <summary>
    /// <paramref name="tile"/> as the pixels it covers, with no colour yet: a colour climbs a node's
    /// ancestors, so it is asked for only once the quad is known to show (see <see cref="CushionQuads"/>).
    ///
    /// <para>The edges are rounded, not truncated, so two shapes that share an edge share it on the
    /// canvas too, with no gap and no overlap between them.</para>
    /// </summary>
    /// <param name="finish">How the drawing's shapes are finished, which says whether this one rises.</param>
    public static CushionQuad Of(ExploreTile tile, ShapeFinish finish) => new(
        (int)MathF.Round(tile.X),
        (int)MathF.Round(tile.Y),
        (int)MathF.Round(tile.X + tile.Width),
        (int)MathF.Round(tile.Y + tile.Height),
        RidgeOf(tile, finish),
        default,
        tile.Framed);

    /// <summary>
    /// How far <paramref name="tile"/>'s surface tilts at its edges, or zero where it is drawn flat. A
    /// bevel and a sheen read only whether it is above zero.
    /// </summary>
    private static float RidgeOf(ExploreTile tile, ShapeFinish finish)
    {
        // A block is not a thing on the disk, and a cushion or a bevel would give it the same
        // physical presence as the files it stands for.
        if (!tile.IsNode || finish.Shading == MapShading.Flat)
        {
            return 0;
        }

        // A folder with a band has its name written across its top edge, where a cushion is at its
        // brightest on the left and its darkest on the right, so text chosen to contrast with the
        // folder's colour would be chosen against a colour the band is not. The band and the gap
        // already say where the folder is, which is the job the cushion was doing. A sheen is at its
        // lightest along the top, where the band is, and at a deep relief a third lighter than the
        // colour the text was chosen against, so a banded folder is flat under a sheen too. A bevel
        // leaves the band flat behind the name, so a folder keeps its bevel.
        if (tile.Header > 0 && finish.Shading is MapShading.Cushion or MapShading.Gradient)
        {
            return 0;
        }

        return (float)CushionShading.RidgeAt(tile.Depth) * finish.Relief;
    }

    /// <summary>
    /// This quad in <paramref name="colour"/>, with its bevel's colours where <paramref name="finish"/>
    /// gives it a bevel. Worked out here once for both routes to the screen, so the shader reads the
    /// colours rather than knowing how each is mixed.
    /// </summary>
    public CushionQuad Coloured(TileColour colour, ShapeFinish finish) =>
        finish.Shading == MapShading.Bevel && Ridge > 0
            ? new(Left, Top, Right, Bottom, Ridge, colour, Framed, finish.Lit(colour), finish.Shaded(colour))
            : new(Left, Top, Right, Bottom, Ridge, colour, Framed);

    /// <summary>
    /// Whether the quad covers any pixel of a canvas <paramref name="width"/> by <paramref name="height"/>,
    /// with its line running <paramref name="reach"/> pixels past its bottom and its right
    /// (<see cref="ShapeBorders.ReachFor"/>).
    /// </summary>
    public bool Reaches(int width, int height, int reach) =>
        Right > Left && Bottom > Top && Right + reach > 0 && Bottom + reach > 0 && Left < width && Top < height;
}
