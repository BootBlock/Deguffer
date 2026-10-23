using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The labels a drawing of rectangles lays over its finished bitmap: each framed folder's name in
/// the band along its top, and a name inside each shape with nothing drawn in it.
///
/// <para>A shape that has children drawn inside it and no band gets no label. Its children are
/// inset by a pixel or two, so its label and its first child's would land within a few pixels of
/// each other and overprint into an unreadable stack. The band is what makes a folder's own name
/// affordable, because nothing is drawn in it.</para>
///
/// <para>The largest shapes are named first, in both kinds. The layout emits shapes depth first,
/// so taking them in that order would spend the whole allowance on the first branch and leave
/// the largest folder elsewhere unnamed.</para>
///
/// <para>Every one of those decisions is made on the part of a shape that is on the canvas. A
/// zoomed treemap's shapes run off its edges, and a name placed at a shape's own left edge would
/// be off the canvas with it, or a shape judged by its whole size would be named in a sliver too
/// small to read. So a folder whose band runs off the left is named where the band comes on, and
/// a band that is off the canvas altogether is not named.</para>
/// </summary>
/// <param name="maximumLabels">How many shapes to name inside at most: see <see cref="ExploreSurface.MaximumLabels"/>.</param>
/// <param name="textColourOf">The colour a shape's name is drawn in, which is its drawing's to say.</param>
internal sealed class TileLabels(
    IReadOnlyList<ExploreTile> tiles,
    ISizedTree tree,
    int root,
    int width,
    int height,
    LayoutLimits limits,
    bool shapeNames,
    int maximumLabels,
    Func<ExploreTile, TileColour> textColourOf)
{
    /// <summary>
    /// How many folders to name in their frames at most, on top of the labels inside the shapes.
    ///
    /// <para>Separate from <see cref="ExploreSurface.MaximumLabels"/>, which keeps the text over the
    /// picture from becoming noise. A folder's name is in a band the layout set aside for it, so it
    /// covers nothing, and a band left empty reads as a folder with no name — so this is set high
    /// enough that a real drive at 4K does not reach it. A band needs a folder at least three lines
    /// of text tall and a name wide, which a 3840 by 2160 canvas fits under two thousand of side by
    /// side; nesting and the mix of sizes on a real drive leave far fewer. This bounds the controls
    /// the shell creates for a canvas that somehow has more, and the smallest folders are the ones
    /// that go without.</para>
    /// </summary>
    private const int MaximumHeaders = 1024;

    /// <summary>The labels, folders' names first, then the names inside the shapes.</summary>
    public IReadOnlyList<ExploreLabel> Build()
    {
        var (headers, insides) = Candidates();
        var labels = new List<ExploreLabel>(Math.Min(headers.Count, MaximumHeaders) + Math.Min(insides.Count, maximumLabels));

        Largest(headers, MaximumHeaders);
        Largest(insides, maximumLabels);

        for (var i = 0; i < headers.Count; i++)
        {
            labels.Add(HeaderLabel(headers[i]));
        }

        for (var i = 0; i < insides.Count; i++)
        {
            labels.Add(InsideLabel(insides[i]));
        }

        return labels;
    }

    /// <summary>
    /// A shape to be named, with the part of it on the canvas, which every decision about its name is
    /// made on: worked out once, rather than again for each comparison of the sort and for the label.
    /// </summary>
    private readonly record struct Named(ExploreTile Tile, ExploreTile Shown)
    {
        public float Area => Shown.Width * Shown.Height;
    }

    /// <summary>Largest shape on the canvas first, for <see cref="Largest"/>.</summary>
    private static readonly Comparison<Named> LargestFirst = (a, b) => b.Area.CompareTo(a.Area);

    /// <summary>
    /// The folders whose band has room for their name, and the shapes with room for a name inside
    /// them and nothing drawn in them, in the order the layout emitted them.
    ///
    /// <para>Indexed rather than foreached. A treemap of a real volume is tens of thousands of
    /// rectangles, and enumerating an IReadOnlyList boxes the list's own struct enumerator and
    /// dispatches every step through the interface: the same cost TileHitTest dropped its iterator
    /// to avoid, on the same list and once per repaint (G5).</para>
    /// </summary>
    private (List<Named> Headers, List<Named> Insides) Candidates()
    {
        var covered = Covered();
        var headers = new List<Named>();
        var insides = new List<Named>();

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var shown = OnCanvas(tile);

            if (tile.Header > 0)
            {
                // A name wide, as the layout asked of the whole band before it gave one. A band cut
                // to a sliver by the canvas's edge is not named, and a name narrower than its own
                // padding would come out with a negative width.
                if (tile.Y + tile.Header > 0 && tile.Y < height && shown.Width >= limits.MinimumLabelWidth)
                {
                    headers.Add(new Named(tile, shown));
                }
            }
            else if (shapeNames
                && !tile.IsAggregate
                && (tile.IsFreeSpace || (tile.Node != root && !covered.Contains(tile.Node)))
                && shown.HasRoomForALabel(limits))
            {
                insides.Add(new Named(tile, shown));
            }
        }

        return (headers, insides);
    }

    /// <summary>
    /// Which nodes had a rectangle drawn inside them. The layouts emit a parent before its children,
    /// so this is complete for every tile by the time <see cref="Candidates"/> reaches it.
    /// </summary>
    private HashSet<int> Covered()
    {
        var covered = new HashSet<int>();

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];

            if (tile.IsNode && tile.Node != root)
            {
                covered.Add(tree.ParentOf(tile.Node));
            }
        }

        return covered;
    }

    /// <summary>
    /// A folder's name, centred in its band, which is one line of text with the gap shared above and
    /// below it. Along the band from where it comes on the canvas, and down from the band's own top,
    /// which moves with the folder as a zoom scrolls it away.
    /// </summary>
    private ExploreLabel HeaderLabel(Named named)
    {
        var (tile, shown) = named;

        return new ExploreLabel(
            tile.Node,
            shown.X + limits.LabelPadding,
            tile.Y + ((tile.Header - limits.MinimumLabelHeight) / 2),
            shown.Width - (limits.LabelPadding * 2),
            Rotation: 0,
            Centred: false,
            textColourOf(tile),
            tile.Bytes,
            new LabelRoom(tile.X, tile.Y, tile.X + tile.Width, tile.Y + tile.Header));
    }

    /// <summary>A shape's name, at the top left of the part of it on the canvas.</summary>
    private ExploreLabel InsideLabel(Named named)
    {
        var (tile, shown) = named;

        return new ExploreLabel(
            tile.Node,
            shown.X + limits.LabelPadding,
            shown.Y + (limits.LabelPadding / 2),
            shown.Width - (limits.LabelPadding * 2),
            Rotation: 0,
            Centred: false,
            textColourOf(tile),
            tile.Bytes,
            new LabelRoom(tile.X, tile.Y, tile.X + tile.Width, tile.Y + tile.Height));
    }

    /// <summary>Keeps at most <paramref name="count"/> of <paramref name="named"/>, largest shape on the canvas first.</summary>
    private static void Largest(List<Named> named, int count)
    {
        named.Sort(LargestFirst);

        if (named.Count > count)
        {
            named.RemoveRange(count, named.Count - count);
        }
    }

    /// <summary>
    /// The part of <paramref name="tile"/> on the canvas, which is all of it unless the picture is
    /// zoomed. Empty where none of it is.
    /// </summary>
    private ExploreTile OnCanvas(ExploreTile tile)
    {
        var left = Math.Max(0, tile.X);
        var top = Math.Max(0, tile.Y);
        var right = Math.Min(width, tile.X + tile.Width);
        var bottom = Math.Min(height, tile.Y + tile.Height);

        return tile with
        {
            X = left,
            Y = top,
            Width = Math.Max(0, right - left),
            Height = Math.Max(0, bottom - top),
        };
    }
}
