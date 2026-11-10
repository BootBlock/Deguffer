namespace Deguffer.Core.Viewing;

/// <summary>
/// Whether any pixel of a rectangle is left to see once it is cut to the screens and every window in
/// front of it is taken away.
/// </summary>
internal static class ScreenCover
{
    /// <summary>
    /// How many pieces the part left to see may be cut into before the answer is given up as "seen".
    /// Each window in front can cut a piece into four, so a desktop of many small overlapping windows
    /// could grow the list without end, and an answer of "seen" only leaves the caller doing what it
    /// did before it asked.
    /// </summary>
    private const int MostPieces = 4096;

    /// <param name="target">The rectangle that would be seen.</param>
    /// <param name="screens">Every monitor's rectangle. Nothing outside all of them can be seen.</param>
    /// <param name="covers">Every window in front of it that hides what is behind it, in any order.</param>
    public static bool IsHidden(ScreenRect target, IReadOnlyList<ScreenRect> screens, IEnumerable<ScreenRect> covers)
    {
        var pieces = new List<ScreenRect>();

        foreach (var screen in screens)
        {
            Keep(pieces, target.Intersect(screen));
        }

        if (pieces.Count == 0)
        {
            return true;
        }

        var cut = new List<ScreenRect>();

        foreach (var cover in covers)
        {
            cut.Clear();

            foreach (var piece in pieces)
            {
                Subtract(piece, cover, cut);
            }

            if (cut.Count > MostPieces)
            {
                return false;
            }

            if (cut.Count == 0)
            {
                return true;
            }

            (pieces, cut) = (cut, pieces);
        }

        return false;
    }

    /// <summary>
    /// What is left of <paramref name="piece"/> outside <paramref name="cover"/>: the bands above and
    /// below the overlap across the piece's whole width, and the parts beside it within its rows.
    /// </summary>
    private static void Subtract(ScreenRect piece, ScreenRect cover, List<ScreenRect> into)
    {
        var overlap = piece.Intersect(cover);

        if (overlap.IsEmpty)
        {
            into.Add(piece);
            return;
        }

        Keep(into, piece with { Bottom = overlap.Top });
        Keep(into, piece with { Top = overlap.Bottom });
        Keep(into, new ScreenRect(piece.Left, overlap.Top, overlap.Left, overlap.Bottom));
        Keep(into, new ScreenRect(overlap.Right, overlap.Top, piece.Right, overlap.Bottom));
    }

    private static void Keep(List<ScreenRect> pieces, ScreenRect piece)
    {
        if (!piece.IsEmpty)
        {
            pieces.Add(piece);
        }
    }
}
