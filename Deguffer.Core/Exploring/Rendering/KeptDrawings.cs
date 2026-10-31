using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The rules for the drawings a map keeps of its picture: which shows over which, which surface the
/// next drawing lands on, and which drawings a new one covers. Pure, so the shell only acts on them.
///
/// <para>The drawing the map works from (the current one) is on top, under only a drawing landing to
/// replace it. The others are under it by how far they are zoomed, finest uppermost, so where one runs
/// out the next finest shows through.</para>
/// </summary>
public static class KeptDrawings
{
    /// <summary>
    /// How many zoomed drawings to keep besides the whole picture, the one on screen included.
    ///
    /// <para>Each is a surface the size of the canvas, which is 33 MB at 3840 by 2160. Two keeps the
    /// last place a zoom or a drag stopped at under the one it stops at next: finer than the whole
    /// picture where the reader goes back over it, and shown again outright where they return to
    /// exactly that place.</para>
    /// </summary>
    public const int ZoomedKept = 2;

    /// <summary>
    /// The drawings of <paramref name="drawings"/> that are shown, in the order they stack, lowest first.
    ///
    /// <para>Each is stacked by a rank rather than by its zoom: two drawings a drag apart share a zoom and
    /// would share a place, and which of them showed through would then be the compositor's choice.</para>
    /// </summary>
    /// <param name="current">The drawing the map works from, or null.</param>
    public static IReadOnlyList<T> Stacked<T>(IReadOnlyList<T> drawings, T? current)
        where T : class, IKeptDrawing
    {
        ArgumentNullException.ThrowIfNull(drawings);

        return [.. drawings.Where(drawing => drawing.IsShown).OrderBy(drawing => Rank(drawings, drawing, current))];
    }

    /// <summary>
    /// Whether <paramref name="drawing"/> stacks above <paramref name="other"/>: a drawing landing to
    /// replace the one on top above everything, <paramref name="current"/> above the rest, and each other
    /// above every one coarser than it, or as fine and shown less recently.
    /// </summary>
    private static bool Above<T>(T drawing, T other, T? current)
        where T : class, IKeptDrawing
    {
        var drawingReplaces = drawing is { IsLanding: true, LandsOnTop: true };
        var otherReplaces = other is { IsLanding: true, LandsOnTop: true };

        if (drawingReplaces || otherReplaces)
        {
            return drawingReplaces && !otherReplaces;
        }

        if (ReferenceEquals(drawing, current) || ReferenceEquals(other, current))
        {
            return ReferenceEquals(drawing, current);
        }

        return other.Viewport.Zoom < drawing.Viewport.Zoom
            || (other.Viewport.Zoom == drawing.Viewport.Zoom && other.LastShown < drawing.LastShown);
    }

    /// <summary>The drawing already made of <paramref name="viewport"/> of <paramref name="picture"/>, or null.</summary>
    public static T? Kept<T>(IReadOnlyList<T> drawings, MapViewport viewport, int picture)
        where T : class, IKeptDrawing
    {
        ArgumentNullException.ThrowIfNull(drawings);

        foreach (var drawing in drawings)
        {
            if (drawing.HasDrawing && drawing.Picture == picture && drawing.Viewport == viewport)
            {
                return drawing;
            }
        }

        return null;
    }

    /// <summary>
    /// The surface a drawing <paramref name="width"/> by <paramref name="height"/> of
    /// <paramref name="viewport"/> lands on, or null where a new one is wanted: one no drawing is using,
    /// of that size where there is one; or one of a picture that has changed, least recently shown,
    /// while nothing is fading in over the pictures before it; or where <see cref="ZoomedKept"/> zoomed
    /// drawings are kept and this is another, the zoomed one least recently shown. Never the whole
    /// picture of <paramref name="picture"/>, nor <paramref name="current"/>, which goes on showing
    /// until this one has landed over it.
    /// </summary>
    /// <param name="picture">The picture now.</param>
    /// <param name="fading">
    /// Whether a drawing is fading in over the pictures before it. Those pictures are what shows
    /// through it, so none of their surfaces is taken.
    /// </param>
    public static T? Reusable<T>(
        IReadOnlyList<T> drawings,
        int width,
        int height,
        MapViewport viewport,
        int picture,
        T? current,
        bool fading)
        where T : class, IKeptDrawing
    {
        ArgumentNullException.ThrowIfNull(drawings);

        var reused = Free(drawings, width, height) ?? (fading ? null : LeastRecent(drawings, current, kept => kept.Picture != picture));

        if (reused is null && !viewport.IsWhole && Zoomed(drawings) >= ZoomedKept)
        {
            reused = LeastRecent(drawings, current, kept => !kept.Viewport.IsWhole);
        }

        return reused;
    }

    /// <summary>
    /// Whether the whole picture is missing under a zoomed <paramref name="current"/>, and is not
    /// landing either, so it has to be painted for a move to have something to show wherever it goes.
    /// </summary>
    /// <param name="picture">The picture now.</param>
    public static bool LacksWhole<T>(IReadOnlyList<T> drawings, T? current, int picture)
        where T : class, IKeptDrawing
    {
        ArgumentNullException.ThrowIfNull(drawings);

        if (current is not { Viewport.IsWhole: false })
        {
            return false;
        }

        foreach (var drawing in drawings)
        {
            if (drawing.Picture == picture && drawing.Viewport.IsWhole && drawing.IsShown)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="other"/> goes once <paramref name="cover"/> covers the screen: it is of a
    /// picture before <paramref name="cover"/>'s, is not landing, and is not <paramref name="spared"/>.
    /// A drawing still landing belongs to a redraw that has not arrived, which takes it down itself if
    /// it is superseded.
    /// </summary>
    /// <param name="spared">
    /// The drawing the map works from, where <paramref name="cover"/> may no longer be the drawing it
    /// was: a fade that ends late reads a surface that can since have been taken for a later picture,
    /// and the drawing worked from then must stay. Null where <paramref name="cover"/> is the drawing
    /// arriving or worked from now.
    /// </param>
    public static bool IsCoveredBy(IKeptDrawing other, IKeptDrawing cover, IKeptDrawing? spared)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(cover);

        return other.Picture < cover.Picture && !other.IsLanding && !ReferenceEquals(other, spared);
    }

    /// <summary>How many drawings stack under <paramref name="drawing"/>. See <see cref="Above"/>.</summary>
    private static int Rank<T>(IReadOnlyList<T> drawings, T drawing, T? current)
        where T : class, IKeptDrawing
    {
        var rank = 0;

        foreach (var other in drawings)
        {
            if (!ReferenceEquals(other, drawing) && other.IsShown && Above(drawing, other, current))
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>A surface nothing is using, of this size where there is one.</summary>
    private static T? Free<T>(IReadOnlyList<T> drawings, int width, int height)
        where T : class, IKeptDrawing
    {
        T? other = null;

        foreach (var drawing in drawings)
        {
            if (drawing.IsShown)
            {
                continue;
            }

            if (drawing.CanvasWidth == width && drawing.CanvasHeight == height)
            {
                return drawing;
            }

            other ??= drawing;
        }

        return other;
    }

    /// <summary>How many of <paramref name="drawings"/> hold a whole zoomed drawing.</summary>
    private static int Zoomed<T>(IReadOnlyList<T> drawings)
        where T : class, IKeptDrawing
    {
        var count = 0;

        foreach (var drawing in drawings)
        {
            if (drawing.HasDrawing && !drawing.Viewport.IsWhole)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The drawing least recently shown of those that hold a whole drawing, are not
    /// <paramref name="current"/> and meet <paramref name="rule"/>, or null.
    /// </summary>
    private static T? LeastRecent<T>(IReadOnlyList<T> drawings, T? current, Func<T, bool> rule)
        where T : class, IKeptDrawing
    {
        T? least = null;

        foreach (var drawing in drawings)
        {
            if (drawing.HasDrawing && !ReferenceEquals(drawing, current) && rule(drawing)
                && (least is null || drawing.LastShown < least.LastShown))
            {
                least = drawing;
            }
        }

        return least;
    }
}
