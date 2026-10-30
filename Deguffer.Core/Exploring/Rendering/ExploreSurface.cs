using System.Collections.Concurrent;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One drawing of one node of one tree: its geometry, what is under a given point, where the text
/// goes, and how to paint it.
///
/// <para>These four answers vary together and only together — a sunburst is laid out, pointed at,
/// labelled and painted differently from a treemap in one consistent set — so they are one type per
/// drawing rather than four parallel switches in the view (G1). A fifth way of drawing a volume is
/// then a new subclass, not an edit to any of them.</para>
///
/// <para>Any <see cref="ISizedTree"/> can be drawn, which is how a scanned drive and a picture of
/// memory share every layout, hit test and rasteriser. What the colours say is the one thing that
/// depends on the tree, and <see cref="ShapeColours"/> carries it.</para>
///
/// <para>Only what every drawing answers. What only a drawing of rectangles can answer (its
/// rectangles, the one under a point, where a node's sits, and the quads the GPU shades) is on
/// <see cref="TiledSurface"/>, so a caller that needs it asks for that type and a sunburst is never
/// made to answer with nothing.</para>
///
/// <para>In Core rather than in the shell for the usual reason: none of it needs a window, and a
/// decision belongs in Core (G1).</para>
/// </summary>
public abstract class ExploreSurface
{
    /// <summary>
    /// How many labels to draw at most.
    ///
    /// <para>The size threshold already keeps this small on an ordinary tree, but a directory of
    /// several hundred near-equal children defeats it — every shape is then big enough to label and
    /// none of them is interesting. Past a few dozen the labels are noise over the picture anyway,
    /// and the list view is the honest way to read that many names.</para>
    /// </summary>
    protected const int MaximumLabels = 64;

    /// <summary>
    /// Which part of the hue circle each node owns, measured from this drawing's root. See
    /// <see cref="BranchHues"/>.
    /// </summary>
    private readonly BranchHues _hues;

    private readonly ShapeColours _colours;

    /// <summary>Each folder's <see cref="Shapes"/>, once asked. Concurrent because a drawing is made and read on any thread.</summary>
    private readonly ConcurrentDictionary<int, IReadOnlyList<MapShape>> _held = new();

    /// <summary>The shapes at the top of the picture, once asked.</summary>
    private IReadOnlyList<MapShape>? _top;

    /// <param name="style">How every shape is finished, and what text is written on them.</param>
    protected ExploreSurface(
        ISizedTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ShapeColours colours,
        MapStyle style,
        MapViewport? viewport)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(style);

        colours.EnsureDescribes(tree);

        Tree = tree;
        Root = root;
        Width = width;
        Height = height;
        Viewport = viewport;
        Limits = limits;
        Style = style;
        _colours = colours;
        _hues = new BranchHues(tree, root);

        // One device pixel at 100%, and as many as the display's scale makes of one, so a line or a
        // bevel is as heavy on a screen at 200% as on one at 100%. The scale is the one the limits
        // were measured at (LayoutLimits.At), which the row height carries untouched by the reader's
        // text size.
        var pixel = Math.Max(1, (int)MathF.Round(limits.RowHeight / LayoutLimits.Default.RowHeight));

        Finish = colours.Contrast is { } contrast ? ShapeFinish.Contrasted(contrast, pixel) : ShapeFinish.Of(style, pixel);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The part of the whole picture this drawing shows, or null where this way of drawing cannot be
    /// zoomed and it shows all of it.
    ///
    /// <para>Said by the drawing rather than assumed by the caller, because the caller asks for a view
    /// and does not always get it: a scan still running is drawn as an icicle whichever view was
    /// picked (see <see cref="Create(ISizedTree, int, ExploreView, int, int, double, double, ShapeColours, ExploreSpacing, VolumeSpace, MapViewport, MapStyle)"/>).
    /// A caller that took its own zoom as what was drawn would place the picture, and resolve a click
    /// on it, for a zoom the picture does not have (§7.1).</para>
    /// </summary>
    public MapViewport? Viewport { get; }

    /// <summary>
    /// How far the map can zoom while this drawing is on screen, or null where it cannot be zoomed:
    /// null exactly where <see cref="Viewport"/> is (see <see cref="MapCeiling"/>).
    ///
    /// <para>Said by the drawing because only it knows what in view it could not draw one by one, and
    /// how small what it drew is, and what it drew is what the screen shows: a ceiling worked out from
    /// anything else would stop a zoom short of detail the reader can see is there, or let it run on
    /// past the last shape.</para>
    /// </summary>
    public abstract double? Ceiling { get; }

    /// <summary>
    /// Where the text goes. At most <see cref="MaximumLabels"/> inside shapes, and for a treemap a
    /// folder's name in each band as well — see <see cref="TiledSurface"/>.
    /// </summary>
    public abstract IReadOnlyList<ExploreLabel> Labels { get; }

    /// <summary>
    /// The tree drawn. A caller resolving a point on this drawing to a node needs it, because the
    /// node is a number in this tree and means nothing in another.
    /// </summary>
    public ISizedTree Tree { get; }

    /// <summary>The node being drawn, which is the whole canvas rather than the tree's own root.</summary>
    protected int Root { get; }

    protected LayoutLimits Limits { get; }

    /// <summary>How every shape is finished, and what text is written on them.</summary>
    protected MapStyle Style { get; }

    /// <summary>
    /// How every shape of this drawing is finished, on this display: the style asked for, or in a high
    /// contrast theme's colours, every shape flat with a line of the theme's text colour inside its
    /// sides, whatever the style. See <see cref="ShapeFinish"/>.
    /// </summary>
    public ShapeFinish Finish { get; }

    /// <summary>
    /// The picture <paramref name="tree"/> is actually drawn as when <paramref name="view"/> is
    /// asked for.
    ///
    /// <para>A tree still being filled in orders its children by name rather than by size, so that a
    /// growing child widens where it is instead of moving. Two of the four drawings cannot be made
    /// from that at all and say so by refusing: squarification is defined over a decreasing
    /// sequence, and a sunburst's residual wedge assumes the small children are the tail. So a scan
    /// in progress draws the icicle whichever view was picked, and the chosen one arrives with the
    /// finished scan. Public because the page has to say so, and has to say it from this rule rather
    /// than from a copy of it: a substitution nobody is told about reads as a bug.</para>
    /// </summary>
    public static ExploreView Drawn(ISizedTree tree, ExploreView view)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return tree.ChildOrder == ExploreChildOrder.BySize ? view : ExploreView.Icicle;
    }

    /// <summary>
    /// Whether <paramref name="view"/> shows <paramref name="tree"/> as a picture that can be zoomed:
    /// the treemap, drawn as one. Not the icicle or the sunburst, which draw the whole picture whatever
    /// they are asked, nor a view that shows no map at all, though the drawing kept behind it is a
    /// treemap: nobody can see a zoom on it. Asked before drawing by a caller with a zoom to hand
    /// over, so a picture that would only refuse it is not placed at it while it is drawn.
    /// </summary>
    public static bool Zooms(ISizedTree tree, ExploreView view) =>
        view == ExploreView.Treemap && Drawn(tree, view) == ExploreView.Treemap;

    /// <summary>
    /// Lay <paramref name="root"/> of <paramref name="tree"/> out for <paramref name="view"/>, on a
    /// canvas of <paramref name="width"/> by <paramref name="height"/> device pixels at
    /// <paramref name="scale"/>.
    ///
    /// <para>The scale is applied to the thresholds here rather than by the caller, because every
    /// one of them is stated in device-independent pixels and a layout measured in device pixels
    /// compared against raw constants draws half-size detail on a high-DPI display.</para>
    /// </summary>
    /// <param name="textScale">
    /// The reader's Windows text size, 1 at 100%. The labels grow with it, so the thresholds for
    /// where a label fits grow with it too — see <see cref="LayoutLimits.ForText"/>.
    /// </param>
    /// <param name="colours">What the colours are to say.</param>
    /// <param name="spacing">How much room a treemap leaves round what each folder holds.</param>
    /// <param name="volume">
    /// The volume to draw beside <paramref name="root"/>, or <see cref="VolumeSpace.None"/>. The caller
    /// decides whether the reader is on the volume or inside its root (see
    /// <see cref="ExplorePosition.Beside"/>); this only refuses it beside anything but the tree's own
    /// root, and only the treemap draws it: free space is in proportion to a whole volume and to
    /// nothing inside it, so a folder the reader has opened is drawn without it.
    /// </param>
    /// <param name="viewport">
    /// The part of the picture to show. Only the treemap can be zoomed, and every other drawing shows
    /// the whole picture whatever this asks; <see cref="Viewport"/> says which happened.
    /// </param>
    /// <param name="style">
    /// How every shape is finished and what is written on them, or null for
    /// <see cref="MapStyle.Default"/>. Its folder bands are part of the treemap's layout as well.
    /// </param>
    public static ExploreSurface Create(
        ISizedTree tree,
        int root,
        ExploreView view,
        int width,
        int height,
        double scale,
        double textScale,
        ShapeColours colours,
        ExploreSpacing spacing,
        VolumeSpace volume,
        MapViewport viewport = default,
        MapStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var styled = style ?? MapStyle.Default;
        var limits = LayoutLimits.Default.Spaced(spacing).Banded(styled.FolderNames).ForText(textScale).At(scale);
        var beside = root == tree.RootNode ? volume : VolumeSpace.None;

        return Drawn(tree, view) switch
        {
            ExploreView.Sunburst => new SunburstSurface(
                tree, root, width, height, limits, colours, styled),
            ExploreView.Icicle => new TiledSurface(
                tree, root, width, height, limits, colours, styled,
                IcicleLayout.Compute(tree, root, width, height, limits)),

            // Including List, Tree and Files, none of which draws a map at all. A page hides the map
            // rather than telling it to stop, so this is the drawing it will be showing again when
            // the user switches back, and it is the one they last saw.
            _ => new TiledSurface(
                tree, root, width, height, limits, colours, styled,
                TreemapLayout.Compute(tree, root, width, height, limits, beside, viewport),
                viewport,
                beside),
        };
    }

    /// <summary>
    /// What paints this drawing on <paramref name="background"/>, a region at a time. Made once per
    /// paint of the drawing, because it works out every shape's colour before it paints a pixel.
    /// </summary>
    public abstract CanvasPainter Painter(TileColour background);

    /// <summary>What is at this canvas point, or null where the point is over nothing.</summary>
    public abstract ExploreHit? At(float x, float y);

    /// <summary>
    /// Whether anything is drawn beside the root: the volume's free space, its use the scan did not
    /// account for, or a part of that use Windows states. Only a treemap of a whole volume draws
    /// any of them, and only where it has room. Said of the whole picture, so a zoom that has left
    /// them all off the canvas still answers yes.
    /// </summary>
    public abstract bool HasVolumeBeside { get; }

    /// <summary>
    /// The shapes this drawing draws directly in <paramref name="folder"/>, in the order it draws them:
    /// what the folder holds that has a shape of its own, largest first, then the block standing for the
    /// rest where there is one. For a null folder, the top of the picture: what the drawing's root
    /// holds, or where a volume is drawn beside the root, the root's own shape and the blocks beside it.
    /// None where the folder is not drawn, or is drawn as one block.
    ///
    /// <para>What the keyboard moves between and a screen reader lists. A treemap that can be zoomed
    /// gives the shapes of its whole picture at its zoom, off the canvas as well as on it, so a shape the
    /// camera has left behind is still there to move to.</para>
    ///
    /// <para>Found once a folder for the life of the drawing, which never changes. A screen reader
    /// asks for the same folder's shapes again on each step through them, and a search of the window
    /// asks for every folder's, while each answer costs a pass over the drawing or a layout of the
    /// folders above (G4).</para>
    /// </summary>
    public IReadOnlyList<MapShape> Shapes(int? folder) =>
        folder is { } inside
            ? _held.GetOrAdd(inside, static (held, drawing) => drawing.FindShapes(held), this)
            : LazyInitializer.EnsureInitialized(ref _top, () => FindShapes(null));

    /// <summary>The shapes <see cref="Shapes"/> answers with, found afresh. Asked at most once a folder.</summary>
    protected abstract IReadOnlyList<MapShape> FindShapes(int? folder);

    /// <summary>
    /// The outline of <paramref name="shape"/>, one of <see cref="Shapes"/>, in canvas pixels: what marks
    /// out the shape the keyboard is on. A block too, which <see cref="Outlines"/> never gives, because the
    /// keyboard reaches a block to hear what it is even though nothing acts on it (§7.1).
    /// </summary>
    public abstract IReadOnlyList<ExploreOutline> OutlineOf(MapShape shape);

    /// <summary>
    /// The node whose shape shows <paramref name="node"/> on this drawing: its own, or where it is not
    /// drawn one by one, the deepest folder above it that is. Null where it is not under this drawing's
    /// root. What a card beside the map lights for a folder, so it lights where the screen shows it.
    /// </summary>
    public abstract int? ShownAs(int node);

    /// <summary>
    /// Where each node <paramref name="nodes"/> takes was drawn with nothing drawn inside its shape: the
    /// shapes whose own colour is what the screen shows, which are what a card lighting every shape of
    /// one kind lights. A folder with what it holds drawn inside it is left out, because lighting it
    /// would light everything inside it whatever its kind. Never an aggregate, as for
    /// <see cref="Outlines"/>.
    ///
    /// <para>A test rather than a set, because the nodes are every one of a kind in the tree, and the
    /// shapes are far fewer than that (G4).</para>
    /// </summary>
    public abstract IReadOnlyList<ExploreOutline> Uncovered(Func<int, bool> nodes);

    /// <summary>
    /// Where a screen showing <paramref name="node"/> drawn on its own at <paramref name="opened"/>
    /// lies on a screen showing this drawing at <paramref name="shown"/>, in fractions of the second:
    /// the shape a change of folder on the map flies into, or pulls back out of. Null where this
    /// drawing cannot be zoomed or does not draw the node's branch.
    /// </summary>
    public abstract MapFrame? ScreenOf(int node, MapViewport shown, MapViewport opened);

    /// <summary>
    /// Where each of <paramref name="nodes"/> was drawn, for a caller that wants to mark it out.
    ///
    /// <para>Only the nodes this drawing actually drew come back, so a selection made in a folder
    /// the user has since left, or one below the depth this view descends to, is silently absent
    /// rather than an error. An aggregate is never outlined: it stands for a run of siblings rather
    /// than for anything the user could have picked (§7.1). A node whose shape has more than one
    /// boundary comes back once per boundary — see <see cref="ExploreOutline"/>.</para>
    ///
    /// <para>A set rather than a list, because the answer is found by one pass over the shapes and
    /// the alternative is a pass per node. A treemap of a volume is tens of thousands of shapes and
    /// the list view selects any number of rows at once (G4).</para>
    /// </summary>
    public abstract IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes);

    /// <summary>
    /// Each node this drawing draws a shape of, once. Not the blocks standing for the rest of the
    /// volume, nor the lumps of shapes too small to draw, which stand for no node.
    /// </summary>
    public abstract IEnumerable<int> NodesDrawn { get; }

    /// <summary>
    /// The shapes of each of <paramref name="nodes"/> this drawing draws, for a change of view to carry
    /// from one drawing to another (<see cref="Layout.ViewMorph"/>). A node it did not draw is absent.
    /// </summary>
    public abstract IReadOnlyList<MorphShape> MorphShapes(IReadOnlySet<int> nodes);

    /// <summary>
    /// Which part of the hue circle a node owns, so a whole subtree shares one part of it.
    ///
    /// <para>Measured from this drawing's root rather than carried in the shape, because the same
    /// folder owns the whole circle when it is opened and one arc of a larger one when it is seen
    /// from above.</para>
    /// </summary>
    internal BranchHue HueOf(int node) => _hues.Of(node);

    /// <summary>
    /// <paramref name="node"/> and every folder above it, up to this drawing's root, or up to the
    /// tree's own root for a node outside this drawing. Gathered for each ask rather than kept, because
    /// it is asked once for a change of folder or a card row lit.
    /// </summary>
    protected HashSet<int> PathOf(int node)
    {
        var path = new HashSet<int>();

        for (var current = node; ; current = Tree.ParentOf(current))
        {
            path.Add(current);

            if (current == Root || Tree.ParentOf(current) == current)
            {
                return path;
            }
        }
    }

    /// <summary>
    /// What the shape for this node at this depth is painted.
    ///
    /// <para>Every colour decision the drawing makes, in one place. The rasterisers are handed this
    /// and do no palette work of their own, so a third way of colouring is a
    /// <see cref="ShapeColours"/> rather than an edit to each of them — and, more to the point, the
    /// labels cannot come out contrasted against a colour the shape underneath was not painted in.</para>
    ///
    /// <para>No block is ever coloured by either scheme. An aggregate stands for a run of siblings
    /// too small to draw, and the others for the rest of the volume, so none belongs to a branch
    /// or has a date. Giving one a colour that reads as a thing on the disk would invite the user to
    /// act on it.</para>
    ///
    /// <para>In a high contrast theme's colours every shape and block is the theme's window colour, and
    /// only its edges tell it from its neighbour (<see cref="Finish"/>).</para>
    ///
    /// <para>A folder drawn round what it holds is the neutral frame grey where the style asks for
    /// grey frames, whatever the colouring says of it, so the colours on the picture are what the
    /// folders hold. Where the style asks for tinted frames it is its own colour mixed half way towards
    /// that grey, so it recedes as a grey one does and still says which branch it is.</para>
    /// </summary>
    /// <param name="framed">Whether the shape is a folder drawn round what it holds.</param>
    protected TileColour ColourFor(int node, int depth, bool framed = false) => _colours.Contrast is { } contrast
        ? contrast.Window
        : node switch
        {
            ExploreTile.Aggregated => TilePalette.Aggregate,
            ExploreTile.FreeSpace => TilePalette.FreeSpace,
            ExploreTile.Unaccounted => TilePalette.Unaccounted,
            ExploreTile.ShadowCopies => TilePalette.ShadowCopies,
            ExploreTile.ReservedStorage => TilePalette.ReservedStorage,
            _ when framed && Style.FolderFill == MapFolderFill.Grey => TilePalette.FolderFrame,
            _ when framed && Style.FolderFill == MapFolderFill.Tinted =>
                ShapeFinish.Mix(_colours.For(this, node, depth), TilePalette.FolderFrame, ShapeFinish.Whole / 2),
            _ => _colours.For(this, node, depth),
        };

    /// <summary>What a rectangle of this drawing is painted. See <see cref="ColourFor(int, int, bool)"/>.</summary>
    protected TileColour ColourOf(ExploreTile tile) => ColourFor(tile.Node, tile.Depth, tile.Framed);

    /// <summary>
    /// The colour text over a shape of this node at this depth has to be drawn in: in a high contrast
    /// theme's colours, the theme's own text colour, which every shape shares.
    /// </summary>
    /// <param name="framed">Whether the shape is a folder drawn round what it holds.</param>
    protected TileColour TextColourFor(int node, int depth, bool framed = false) =>
        _colours.Contrast is { } contrast ? contrast.Text : ColourFor(node, depth, framed).ContrastingText;
}
