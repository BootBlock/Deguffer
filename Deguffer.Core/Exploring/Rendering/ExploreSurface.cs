using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the pointer found: a node, the block standing in for items too small to draw, or one of the
/// blocks standing for the rest of the volume.
/// </summary>
/// <param name="Bytes">
/// What was pointed at accounts for this much. Carried rather than looked up because none of the
/// blocks has a node to look it up from.
/// </param>
public readonly record struct ExploreHit(int Node, long Bytes)
{
    public bool IsAggregate => Node == ExploreTile.Aggregated;

    public bool IsFreeSpace => Node == ExploreTile.FreeSpace;

    public bool IsUnaccounted => Node == ExploreTile.Unaccounted;

    public bool IsShadowCopies => Node == ExploreTile.ShadowCopies;

    public bool IsReservedStorage => Node == ExploreTile.ReservedStorage;

    /// <summary>
    /// Whether what was pointed at is a node of the tree, and so something a click may pick (§7.1).
    /// None of the blocks is.
    /// </summary>
    public bool IsNode => Node >= 0;
}

/// <summary>
/// Where a view should put one piece of text, in canvas pixels.
///
/// <para>Positions rather than drawn text, because the labels are the one part of the picture that
/// is not in the bitmap: they are real controls laid over it, so they scale with the user's text
/// size and a screen reader can reach them. What belongs here is the part that is geometry, and
/// what belongs to the shell is the part that is a control.</para>
/// </summary>
/// <param name="X">The left edge of a box one line high. The text is trimmed to fit it.</param>
/// <param name="Y">The top of that box.</param>
/// <param name="Rotation">
/// How far to turn the text about the middle of its box, in degrees clockwise. Zero for anything
/// laid out in rectangles; a sunburst turns each label to lie along its own ring.
/// </param>
/// <param name="Centred">Whether the text sits in the middle of its box or starts at the left of it.</param>
/// <param name="Colour">
/// What colour the text has to be to stay legible against the shape underneath it. Decided here
/// because the surface is what knows the colour it painted that shape in.
/// </param>
/// <param name="Bytes">
/// What the shape accounts for. Carried for the blocks that are not nodes, whose figure a caption
/// cannot look up in the tree.
/// </param>
/// <param name="Room">
/// Where the text has to stay to be over its own shape and no other: the shape, or for a folder's
/// name the band set aside for it. What a picture magnified less than it was drawn at fades the
/// label against (see <see cref="Fade"/>).
/// </param>
public readonly record struct ExploreLabel(
    int Node,
    float X,
    float Y,
    float Width,
    float Rotation,
    bool Centred,
    TileColour Colour,
    long Bytes,
    LabelRoom Room)
{
    /// <summary>
    /// How much smaller than where it is gone a label is still whole: it fades over the last fifth of
    /// the way its shape shrinks before the text reaches the shape's edge. Enough to read as a fade at
    /// the speed of a zoom, and short enough that a name is never faint while its shape still has
    /// plenty of room for it.
    /// </summary>
    public const float FadeRange = 1.25f;

    /// <summary>
    /// The least magnification a picture at rest can be shown at. One only to within the canvas
    /// rounded to whole device pixels, which is half a pixel across it, and under a 256th of any
    /// canvas wider than 128 pixels. A label is whole down to this, so the rounding never dims one
    /// where its drawing put it, as it would a name that just fits, whose fade would otherwise run
    /// over less than that rounding.
    /// </summary>
    public const float AtRest = 1 - (1f / 256);

    /// <summary>
    /// The least a magnification can be for <see cref="Fade"/> to be measured against it. A text with no
    /// extent fits at any magnification, and a fade has to run between two different ones.
    /// </summary>
    private const float LeastGone = 1f / 1024;

    /// <summary>
    /// The point of the label that stays on its place in the picture as the picture is magnified,
    /// while the text keeps the reader's size: the start of the line, or its middle for a centred
    /// label, along the top of its box.
    /// </summary>
    public (float X, float Y) Anchor => (Centred ? X + (Width / 2) : X, Y);

    /// <summary>
    /// How opaque the label is as its picture is magnified, on each axis, for text
    /// <paramref name="textWidth"/> by <paramref name="textHeight"/> canvas pixels.
    ///
    /// <para>Text keeps its size while the shape it names grows and shrinks with the picture, so
    /// shrinking the picture brings the shape's edges in on it. Where the text reaches an edge it is
    /// gone, so it never lies over a shape that is not its own, and it fades out over the
    /// <see cref="FadeRange"/> before that. A shape that grows takes its name with it and never loses
    /// it.</para>
    ///
    /// <para>The text as measured rather than the box it was laid out in, because a short name in a
    /// wide shape stays readable long after the box would have run out of room.</para>
    /// </summary>
    public (LabelFade Across, LabelFade Down) Fade(float textWidth, float textHeight)
    {
        var (anchorX, anchorY) = Anchor;
        var before = Centred ? 0.5f : 0;

        return (
            AxisFade(anchorX - Room.Left, Room.Right - anchorX, textWidth * before, textWidth * (1 - before)),
            AxisFade(anchorY - Room.Top, Room.Bottom - anchorY, 0, textHeight));
    }

    /// <summary>
    /// The fade along one axis, for a text running <paramref name="textBefore"/> before the anchor and
    /// <paramref name="textAfter"/> after it, in a room reaching <paramref name="roomBefore"/> and
    /// <paramref name="roomAfter"/> from it.
    ///
    /// <para>Magnified by <c>m</c>, the room reaches <c>m</c> times as far and the text as far as it
    /// did, so a side is outgrown below the magnification that is their ratio.</para>
    /// </summary>
    private static LabelFade AxisFade(float roomBefore, float roomAfter, float textBefore, float textAfter)
    {
        var gone = Math.Max(Math.Max(Outgrown(textBefore, roomBefore), Outgrown(textAfter, roomAfter)), LeastGone);

        // Already past its room where it was drawn, or within the rounding of it. The layout chose to
        // name it, so it is whole there, as it was before the picture moved, and goes as soon as the
        // picture shrinks.
        return gone >= AtRest
            ? new LabelFade(AtRest / FadeRange, AtRest)
            : new LabelFade(gone, Math.Min(AtRest, gone * FadeRange));
    }

    private static float Outgrown(float text, float room) =>
        text <= 0 ? 0 : room <= 0 ? float.PositiveInfinity : text / room;
}

/// <summary>
/// The rectangle a label's text has to stay inside, in canvas pixels. See
/// <see cref="ExploreLabel.Room"/>.
/// </summary>
public readonly record struct LabelRoom(float Left, float Top, float Right, float Bottom);

/// <summary>
/// How a label's opacity follows the magnification of its picture along one axis, where one is the
/// magnification it was drawn at: none at or below <paramref name="Gone"/>, all of it at or above
/// <paramref name="Whole"/>, and in proportion between. <paramref name="Whole"/> is never above
/// <see cref="ExploreLabel.AtRest"/>, so a label is whole where its drawing put it, and always above
/// <paramref name="Gone"/>.
/// </summary>
public readonly record struct LabelFade(float Gone, float Whole);

/// <summary>One corner of a shape's outline, in canvas pixels.</summary>
public readonly record struct ExplorePoint(float X, float Y);

/// <summary>
/// One closed boundary of one node's shape, for a caller that wants to draw a line round it.
///
/// <para>An outline rather than a filled shape, because the point of it is to say which shape is
/// selected without hiding what the shape says. A wash over the top would change the colour the
/// picture spent its whole palette establishing.</para>
///
/// <para>A polygon rather than a rectangle or a sector, so the shell has one thing to draw for all
/// three views. A sunburst's arcs come out as enough short segments that the curve reads as a
/// curve — a hundredth of a pixel from true at the radii drawn here — and the alternative is a
/// shape union the shell would have to take apart again.</para>
///
/// <para><b>One boundary, not one shape.</b> A ring has two, an inner and an outer, and joining
/// them would draw a radial line across it that is not there. So a node can come back more than
/// once, which is also why each of these names the node it belongs to: the answer is a partial
/// mapping — a node this drawing did not draw is simply absent — and without the name a caller
/// cannot tell which shape it was handed, nor a test tell a right answer from a wrong one.</para>
/// </summary>
public readonly record struct ExploreOutline(int Node, IReadOnlyList<ExplorePoint> Points);

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

    protected ExploreSurface(
        ISizedTree tree,
        int root,
        int width,
        int height,
        LayoutLimits limits,
        ShapeColours colours,
        MapViewport? viewport)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(colours);

        colours.EnsureDescribes(tree);

        Tree = tree;
        Root = root;
        Width = width;
        Height = height;
        Viewport = viewport;
        Limits = limits;
        _colours = colours;
        _hues = new BranchHues(tree, root);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The part of the whole picture this drawing shows, or null where this way of drawing cannot be
    /// zoomed and it shows all of it.
    ///
    /// <para>Said by the drawing rather than assumed by the caller, because the caller asks for a view
    /// and does not always get it: a scan still running is drawn as an icicle whichever view was
    /// picked (see <see cref="Create(ISizedTree, int, ExploreView, int, int, double, double, ShapeColours, ExploreSpacing, VolumeSpace, MapViewport)"/>).
    /// A caller that took its own zoom as what was drawn would place the picture, and resolve a click
    /// on it, for a zoom the picture does not have (§7.1).</para>
    /// </summary>
    public MapViewport? Viewport { get; }

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

    /// <summary>
    /// Lay <paramref name="root"/> of a scanned <paramref name="tree"/> out for
    /// <paramref name="view"/>, coloured to say <paramref name="colouring"/>. See the overload taking
    /// <see cref="ShapeColours"/> for everything else.
    /// </summary>
    /// <param name="scheme">Which set of colours the colouring is drawn in.</param>
    /// <param name="nowUtc">
    /// What "now" is, for the age bands. Passed in rather than read, so a drawing coloured by age is
    /// provable without a clock (G8) — the same seam
    /// <see cref="Scanning.RelativeAge.Describe"/> takes.
    /// </param>
    public static ExploreSurface Create(
        ExploreTree tree,
        int root,
        ExploreView view,
        int width,
        int height,
        double scale,
        double textScale,
        ExploreColouring colouring,
        ExploreScheme scheme,
        DateTime nowUtc,
        ExploreSpacing spacing,
        VolumeSpace volume) =>
        Create(
            tree, root, view, width, height, scale, textScale,
            ShapeColours.For(tree, colouring, scheme, nowUtc, growth: null, types: null),
            spacing, volume);

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
        MapViewport viewport = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var limits = LayoutLimits.Default.Spaced(spacing).ForText(textScale).At(scale);
        var beside = root == tree.RootNode ? volume : VolumeSpace.None;

        return Drawn(tree, view) switch
        {
            ExploreView.Sunburst => new SunburstSurface(
                tree, root, width, height, limits, colours),
            ExploreView.Icicle => new TiledSurface(
                tree, root, width, height, limits, colours,
                IcicleLayout.Compute(tree, root, width, height, limits)),

            // Including List, Tree and Files, none of which draws a map at all. A page hides the map
            // rather than telling it to stop, so this is the drawing it will be showing again when
            // the user switches back, and it is the one they last saw.
            _ => new TiledSurface(
                tree, root, width, height, limits, colours,
                TreemapLayout.Compute(tree, root, width, height, limits, beside, viewport),
                viewport,
                TreemapLayout.DrawsBeside(tree.SizeOf(root), beside, width, height, limits)),
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
    /// The rectangle of the shape at this canvas point, or null where the point is over nothing or
    /// this drawing is not made of rectangles. The shape is the one <see cref="At"/> names there.
    /// </summary>
    public abstract ExploreTile? TileAt(float x, float y);

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
    /// Which part of the hue circle a node owns, so a whole subtree shares one part of it.
    ///
    /// <para>Measured from this drawing's root rather than carried in the shape, because the same
    /// folder owns the whole circle when it is opened and one arc of a larger one when it is seen
    /// from above.</para>
    /// </summary>
    internal BranchHue HueOf(int node) => _hues.Of(node);

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
    /// </summary>
    protected TileColour ColourFor(int node, int depth) => node switch
    {
        ExploreTile.Aggregated => TilePalette.Aggregate,
        ExploreTile.FreeSpace => TilePalette.FreeSpace,
        ExploreTile.Unaccounted => TilePalette.Unaccounted,
        ExploreTile.ShadowCopies => TilePalette.ShadowCopies,
        ExploreTile.ReservedStorage => TilePalette.ReservedStorage,
        _ => _colours.For(this, node, depth),
    };

    /// <summary>The colour text over a shape of this node at this depth has to be drawn in.</summary>
    protected TileColour TextColourFor(int node, int depth) => ColourFor(node, depth).ContrastingText;
}
