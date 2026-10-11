using System.Runtime.InteropServices;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One rectangle of a treemap as it is shaded: the pixels it covers, how far its cushion rises, and
/// its colour. The one place a laid-out shape becomes something to shade, so the CPU and the GPU shade
/// the same shapes from the same edges (see <see cref="CushionQuads"/>).
///
/// <para>Sequential, and 24 bytes, because the GPU reads an array of these as it stands: four edges
/// as integers, the ridge as a float, and the colour's three bytes, with one byte of padding after
/// them that nothing reads.</para>
/// </summary>
/// <param name="Left">The first column covered.</param>
/// <param name="Top">The first row covered.</param>
/// <param name="Right">The column after the last covered.</param>
/// <param name="Bottom">The row after the last covered.</param>
/// <param name="Ridge">How far the surface tilts at the edges: see <see cref="CushionShading.RidgeAt"/>. Zero for a flat shape.</param>
/// <param name="Colour">The colour the light is a fraction of.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CushionQuad(int Left, int Top, int Right, int Bottom, float Ridge, TileColour Colour)
{
    /// <summary>
    /// <paramref name="tile"/> as the pixels it covers, with no colour yet: a colour climbs a node's
    /// ancestors, so it is asked for only once the quad is known to show (see <see cref="CushionQuads"/>).
    ///
    /// <para>The edges are rounded, not truncated, so two shapes that share an edge share it on the
    /// canvas too, with no gap and no overlap between them.</para>
    /// </summary>
    public static CushionQuad Of(ExploreTile tile)
    {
        // Three shapes are drawn flat. Neither block is a thing on the disk, and a cushion would give
        // it the same physical presence as the files it stands for. A folder with a band has its
        // name written across its top edge, where a cushion is at its brightest on the left and its
        // darkest on the right, so text chosen to contrast with the folder's colour would be chosen
        // against a colour the band is not. The band and the gap already say where the folder is,
        // which is the job the cushion was doing.
        var ridge = !tile.IsNode || tile.Header > 0 ? 0 : (float)CushionShading.RidgeAt(tile.Depth);

        return new CushionQuad(
            (int)MathF.Round(tile.X),
            (int)MathF.Round(tile.Y),
            (int)MathF.Round(tile.X + tile.Width),
            (int)MathF.Round(tile.Y + tile.Height),
            ridge,
            default);
    }

    /// <summary>Whether the quad covers any pixel of a canvas <paramref name="width"/> by <paramref name="height"/>.</summary>
    public bool Reaches(int width, int height) =>
        Right > Left && Bottom > Top && Right > 0 && Bottom > 0 && Left < width && Top < height;
}
