using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// Marks out on the drawing the map works from what the page told it of: the picks, the hatch over
/// what a removal is acting on and what a card beside the map lights, and the outlines round the shape
/// under the pointer and the shape the keyboard is on. Which shapes each mark goes round is
/// <see cref="MapMarks"/>'s to say, and the lines are drawn by <see cref="ExploreHighlight"/>.
///
/// <para>Every mark is found again in each new drawing, which is new geometry: the map calls
/// <see cref="Remark"/> after each one, so a kind of mark added here is marked out again everywhere
/// the others are.</para>
///
/// <para>A mark names nodes of the tree the page handed over last. A drawing of an older tree of the
/// same place, still on screen while the new one lands, marks each node out where it draws the node's
/// counterpart (<see cref="MapReading"/>), and a drawing of anything else marks nothing out. The hit test
/// reads the drawing the same way, through <see cref="ReadingOf"/>, so an outline marks the shape a
/// click there picks (§7.1).</para>
/// </summary>
internal sealed class ExploreMarking(ExploreHighlight highlight)
{
    private readonly MapMarks _marks = new();

    /// <summary>The tree the page handed over last, which every mark names nodes of.</summary>
    private ISizedTree? _tree;

    /// <summary>The last reading found, kept while it is of the same two trees. See <see cref="ReadingOf"/>.</summary>
    private MapReading? _reading;

    /// <summary>What a card beside the map lights on it, or null. See <see cref="Light"/>.</summary>
    private MapLight? _light;

    /// <summary>What the user picked.</summary>
    public IReadOnlySet<int> Picked => _marks.Picked;

    /// <summary>Read every mark, and every drawing, in <paramref name="tree"/> from now on.</summary>
    public void ReadIn(ISizedTree? tree) => _tree = tree;

    /// <summary>
    /// Mark <paramref name="nodes"/> out on <paramref name="drawing"/> as what the user picked. False where
    /// they were picked already and nothing changed.
    /// </summary>
    public bool Select(IReadOnlyList<int> nodes, ExploreSurface? drawing)
    {
        if (!_marks.Pick(nodes))
        {
            return false;
        }

        ShowPicked(drawing);
        return true;
    }

    /// <summary>
    /// Hatch <paramref name="nodes"/> of <paramref name="tree"/> on <paramref name="drawing"/> as what a
    /// removal under way is acting on, and nothing once it is over.
    /// </summary>
    public void MarkRemoving(ISizedTree? tree, IReadOnlyList<int> nodes, ExploreSurface? drawing, double scale)
    {
        _marks.MarkRemoving(tree, nodes);

        ShowMarked(drawing, scale);
    }

    /// <summary>
    /// Light what a card beside the map points at on <paramref name="drawing"/>, or nothing where
    /// <paramref name="light"/> is null. Kept and lit again on every new drawing (see <see cref="Remark"/>).
    /// </summary>
    public void Light(MapLight? light, ExploreSurface? drawing)
    {
        _light = light;

        ShowLit(drawing);
    }

    /// <summary>
    /// Mark the picks, the light and the hatch out again on <paramref name="drawing"/>, the drawing the
    /// map works from now, whose geometry places each of them somewhere else.
    /// </summary>
    public void Remark(ExploreSurface? drawing, double scale)
    {
        ShowPicked(drawing);
        ShowLit(drawing);
        ShowMarked(drawing, scale);
    }

    /// <summary>
    /// Draw the accent outline round what the pointer is over, and round the shape the keyboard is on,
    /// where each gets one (see <see cref="MapMarks"/>).
    /// </summary>
    /// <param name="hovered">What the pointer is over, or null.</param>
    /// <param name="tile">The tile <paramref name="hovered"/> was found on while the camera moves, or null where it was found on the drawing.</param>
    /// <param name="focused">The shape the keyboard is on while the keyboard has the map and <paramref name="drawing"/> is of the tree read in, or null.</param>
    /// <param name="surface">What <paramref name="drawing"/> is painted on, which the shape under the pointer is lifted from.</param>
    public void ShowHovered(ExploreSurface? drawing, ExploreHit? hovered, MapTile? tile, MapShape? focused, ICompositionSurface? surface)
    {
        var under = _marks.Hover(hovered);

        if (Outlining(drawing) is not { } outlining)
        {
            highlight.ShowHovered([], null, Vector2.Zero);
            return;
        }

        var canvas = new Vector2(outlining.Drawing.Width, outlining.Drawing.Height);
        var focusOutlines = focused is { } shape && _marks.OutlinesFocus(shape.Node) ? outlining.Drawing.OutlineOf(shape) : [];

        // Found on a tile, the shape may be one the drawing does not draw, so the tile outlines it, in
        // the drawing's canvas. Nothing is lifted: the drawing's surface holds coarser pixels there.
        if (tile is not null && outlining.Drawing.Viewport is { } drawn)
        {
            highlight.ShowHovered(
                [
                    .. under.Count > 0 && ReadingOf(tile.Picture.Tree) is { } inTile
                        ? tile.Outlines(inTile.InDrawing(under), drawn, outlining.Drawing.Width, outlining.Drawing.Height)
                        : [],
                    .. focusOutlines,
                ],
                null,
                canvas);
            return;
        }

        highlight.ShowHovered(
            [.. under.Count > 0 ? outlining.Drawing.Outlines(outlining.Reading.InDrawing(under)) : [], .. focusOutlines],
            surface,
            canvas);
    }

    /// <summary>
    /// How a drawing of <paramref name="drawn"/> is read in the tree read in, or null where it cannot be.
    /// Found once for each pair of trees, because finding nodes again builds an index of each folder it
    /// looks in.
    /// </summary>
    public MapReading? ReadingOf(ISizedTree drawn)
    {
        if (_reading is not { } reading || !ReferenceEquals(reading.Drawn, drawn) || !ReferenceEquals(reading.Current, _tree))
        {
            _reading = MapReading.Of(drawn, _tree);
        }

        return _reading;
    }

    /// <summary>Draw the outline round whatever is picked and <paramref name="drawing"/> actually drew.</summary>
    private void ShowPicked(ExploreSurface? drawing)
    {
        if (Outlining(drawing) is not { } outlining)
        {
            highlight.ShowPicked([], 0);
            return;
        }

        // Twice the whole picture in the drawing's canvas pixels, which is a canvas as many times
        // over as the drawing is zoomed: the picture is within that of the canvas's corner whichever
        // part of it the canvas shows, and so is the margin the outlines are drawn into round it.
        var far = 2 * Math.Max(outlining.Drawing.Width, outlining.Drawing.Height) * (outlining.Drawing.Viewport?.Zoom ?? 1);

        highlight.ShowPicked(outlining.Drawing.Outlines(outlining.Reading.InDrawing(_marks.Picked)), (float)far);
    }

    /// <summary>
    /// Light what a card beside the map is pointing at, and nothing while <paramref name="drawing"/> is
    /// of another tree or a change of folder is still on its way.
    /// </summary>
    private void ShowLit(ExploreSurface? drawing) =>
        highlight.ShowLit(_light is { } light && Outlining(drawing) is { } outlining ? light.On(outlining.Drawing) : null);

    /// <summary>Hatch what a removal under way is acting on, where <paramref name="drawing"/> is of the tree it named.</summary>
    private void ShowMarked(ExploreSurface? drawing, double scale) =>
        highlight.ShowMarked(
            drawing is not null && _marks.RemovingIn(drawing.Tree) is { Count: > 0 } removing ? drawing.Outlines(removing) : [],
            scale);

    /// <summary>
    /// <paramref name="drawing"/> with how it is read in the tree read in, or null where there is no
    /// drawing or it cannot be read there, and nothing is marked out on it.
    /// </summary>
    private (ExploreSurface Drawing, MapReading Reading)? Outlining(ExploreSurface? drawing) =>
        drawing is not null && ReadingOf(drawing.Tree) is { } reading ? (drawing, reading) : null;
}
