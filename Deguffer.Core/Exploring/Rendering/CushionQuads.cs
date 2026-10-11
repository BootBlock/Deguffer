using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The rectangles of one canvas as they are shaded, in the order they are drawn: a folder before what
/// is drawn of it, so a later quad covers an earlier one where they overlap.
///
/// <para>What both routes to the screen shade. The CPU paints them a region at a time with
/// <see cref="TileRasteriser"/>, and the GPU draws them all in one pass. Made once, so neither works
/// out a shape's colour or edges for itself, and the two cannot come to disagree about either.</para>
///
/// <para>Only the quads that cover a pixel of the canvas. A zoomed picture's shapes run far off it,
/// and a shape too thin to cover a pixel once rounded shows nowhere.</para>
/// </summary>
public sealed class CushionQuads
{
    private readonly CushionQuad[] _quads;

    /// <summary>
    /// Get <paramref name="tiles"/> ready to shade on a canvas of <paramref name="width"/> by
    /// <paramref name="height"/>, each painted the colour <paramref name="colourOf"/> gives its node at
    /// its depth.
    /// </summary>
    public CushionQuads(IReadOnlyList<ExploreTile> tiles, int width, int height, Func<int, int, TileColour> colourOf)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(colourOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;

        var quads = new CushionQuad[tiles.Count];
        var count = 0;

        // Through the list's own indexer once per shape, which is the one pass made over it (G4).
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var quad = CushionQuad.Of(tile);

            if (quad.Reaches(width, height))
            {
                quads[count++] = quad with { Colour = colourOf(tile.Node, tile.Depth) };
            }
        }

        _quads = count == quads.Length ? quads : quads[..count];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Every quad that covers a pixel of the canvas, in the order they are drawn.</summary>
    public ReadOnlySpan<CushionQuad> Quads => _quads;

    /// <summary>The quads themselves, for the rasteriser that walks them once per region (G5).</summary>
    internal CushionQuad[] Array => _quads;
}
