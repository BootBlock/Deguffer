using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Drawings whose every point is known: one shape over the whole canvas, so what a point answers
/// says which drawing answered it.
/// </summary>
internal static class Drawings
{
    /// <summary>Two canvases of regions across and down, so some can land while others have not.</summary>
    public const int Size = PaintOrder.RegionSize * 2;

    public static readonly TileColour Ground = new(32, 32, 32);

    /// <summary>A tree of <paramref name="files"/> files under the root, numbered 1 upwards.</summary>
    public static ExploreTree Tree(int files = 2)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, files).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A drawing of <paramref name="tree"/> that is <paramref name="node"/> at every point.</summary>
    public static TiledSurface Covering(ExploreTree tree, int node, MapViewport? viewport = null) =>
        new(
            tree,
            tree.RootNode,
            Size,
            Size,
            LayoutLimits.Default,
            ShapeColours.ByBranch(ExploreScheme.Standard),
            [new ExploreTile(node, Depth: 1, Bytes: 1000, X: 0, Y: 0, Width: Size, Height: Size)],
            viewport);
}

/// <summary>
/// A drawing whose regions are not painted until the test lets them, apart from those
/// <paramref name="painted"/> says may go ahead, so a test can stop a redraw part of the way through
/// landing.
/// </summary>
internal sealed class GatedSurface(TiledSurface drawing, Func<CanvasRegion, bool> painted)
    : ExploreSurface(
        drawing.Tree,
        drawing.Tree.RootNode,
        drawing.Width,
        drawing.Height,
        LayoutLimits.Default,
        ShapeColours.ByBranch(ExploreScheme.Standard),
        drawing.Viewport)
{
    private readonly ManualResetEventSlim _open = new();

    public override IReadOnlyList<ExploreLabel> Labels => drawing.Labels;

    public override bool HasVolumeBeside => drawing.HasVolumeBeside;

    public override double? Ceiling => drawing.Ceiling;

    /// <summary>Let every region held back be painted.</summary>
    public void Open() => _open.Set();

    public override CanvasPainter Painter(TileColour background) =>
        new GatedPainter(drawing.Painter(background), painted, _open, background);

    public override ExploreHit? At(float x, float y) => drawing.At(x, y);

    public override ExploreTile? TileAt(float x, float y) => drawing.TileAt(x, y);

    public override IReadOnlyList<ExploreOutline> Outlines(IReadOnlySet<int> nodes) => drawing.Outlines(nodes);

    private sealed class GatedPainter(
        CanvasPainter painter, Func<CanvasRegion, bool> painted, ManualResetEventSlim open, TileColour background)
        : CanvasPainter(painter.Width, painter.Height, background)
    {
        protected override void Draw(byte[] pixels, CanvasRegion region)
        {
            if (!painted(region) && !open.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException($"The region at {region.X}, {region.Y} was never let through.");
            }

            painter.Paint(pixels, region);
        }
    }
}
