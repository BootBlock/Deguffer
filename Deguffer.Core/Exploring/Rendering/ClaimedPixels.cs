namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Which pixels of one region of a canvas already belong to a shape.
///
/// <para>A treemap's rectangles are nested, so painting them in the layout's order shades every
/// pixel once for its own shape and once again for each level above it. Measured over a tree of a
/// real volume at six levels deep, that is close to seven times the canvas shaded to produce one
/// canvas: <b>85% of the arithmetic is painted over before anybody sees it</b>.</para>
///
/// <para>Painting the deepest shape first and letting whichever shape claims a pixel keep it gives
/// the same picture. Where two shapes overlap, the one later in the layout's order wins under
/// either rule — it is the last to paint going forwards, and the first to claim going backwards —
/// so this is a reordering of the same result rather than a different one.</para>
///
/// <para>One bit per pixel. A byte per pixel would put megabytes on the large-object heap for
/// every region of every repaint, which is the allocation the rasteriser's own buffer contract
/// exists to avoid (G5).</para>
/// </summary>
public sealed class ClaimedPixels
{
    private const int PixelsPerWord = 64;

    private readonly ulong[] _words;

    private int _unclaimed;

    /// <summary>Track the pixels of <paramref name="region"/>.</summary>
    public ClaimedPixels(CanvasRegion region)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(region.Width, nameof(region));
        ArgumentOutOfRangeException.ThrowIfNegative(region.Height, nameof(region));

        Region = region;

        _unclaimed = region.Width * region.Height;
        _words = new ulong[((_unclaimed + PixelsPerWord) - 1) / PixelsPerWord];
    }

    /// <summary>The pixels tracked.</summary>
    public CanvasRegion Region { get; }

    /// <summary>
    /// Whether every pixel of the region is spoken for. Nothing drawn after that can show, so the
    /// caller can stop rather than walk the shapes it has left.
    /// </summary>
    public bool IsFull => _unclaimed == 0;

    /// <summary>
    /// Claim the pixel at <paramref name="x"/>, <paramref name="y"/>, and say whether it was still
    /// free. A pixel already claimed keeps the colour it was given.
    /// </summary>
    public bool Claim(int x, int y)
    {
        var index = ((y - Region.Y) * Region.Width) + (x - Region.X);
        var bit = 1UL << (index % PixelsPerWord);

        ref var word = ref _words[index / PixelsPerWord];

        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        _unclaimed--;

        return true;
    }
}
