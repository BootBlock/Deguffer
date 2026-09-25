using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rectangle a drawing hands over for a point, which is what a double-click opens a folder out of
/// and zooms to. It has to be the shape the pointer names there, or the map opens out of one shape and
/// shows another.
/// </summary>
public sealed class SurfaceTileAtTests
{
    private const int Width = 400;
    private const int Height = 300;

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    public void TheRectangleAtAPointIsTheShapeThePointIsOver(ExploreView view)
    {
        var surface = Surface(view);
        var checkedAny = false;

        for (var x = 1f; x < Width; x += 13)
        {
            for (var y = 1f; y < Height; y += 11)
            {
                var hit = surface.At(x, y);
                var tile = surface.TileAt(x, y);

                Assert.Equal(hit?.Node, tile?.Node);

                if (tile is { } shape)
                {
                    Assert.InRange(x, shape.X, shape.X + shape.Width);
                    Assert.InRange(y, shape.Y, shape.Y + shape.Height);
                    checkedAny = true;
                }
            }
        }

        Assert.True(checkedAny, "no point was over a shape");
    }

    [Fact]
    public void ASunburstHasNoRectangles()
    {
        var surface = Surface(ExploreView.Sunburst);

        Assert.NotNull(surface.At(Width / 2f, Height / 2f));
        Assert.Null(surface.TileAt(Width / 2f, Height / 2f));
    }

    private static ExploreSurface Surface(ExploreView view)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var projects = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [Folder("projects"), Folder("media"), File("pagefile.sys", 3_000_000)]);

        builder.AddChildren(projects, [File("a.bin", 2_000_000), File("b.bin", 1_000_000), File("c.bin", 500_000)]);
        builder.AddChildren(projects + 1, [File("d.mp4", 1_500_000), File("e.mp4", 700_000)]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        return ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ExploreColouring.Branch, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None);
    }

    private static ExploreChild Folder(string name) =>
        new(name, IsDirectory: true, IsLink: false, 0, ExploreTimestamp.Unknown, ExploreTimestamp.Unknown);

    private static ExploreChild File(string name, long bytes) =>
        new(name, IsDirectory: false, IsLink: false, bytes, ExploreTimestamp.Unknown, ExploreTimestamp.Unknown);
}
