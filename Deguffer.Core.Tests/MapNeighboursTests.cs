using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// An arrow key moves the keyboard to the shape next to the one it is on, in that direction: one
/// whose point is past this one's that way, the nearest in line first, and nothing where there is none.
/// </summary>
public sealed class MapNeighboursTests
{
    /// <summary>
    /// A treemap's commonest arrangement: one large shape on the left, two stacked on the right. Right
    /// from the large one goes to the right, down from the top right goes to the one under it, and left
    /// from either goes back.
    /// </summary>
    [Theory]
    [InlineData(0, MapDirection.Right, 1)]
    [InlineData(1, MapDirection.Down, 2)]
    [InlineData(2, MapDirection.Up, 1)]
    [InlineData(1, MapDirection.Left, 0)]
    [InlineData(2, MapDirection.Left, 0)]
    [InlineData(0, MapDirection.Left, null)]
    [InlineData(0, MapDirection.Up, null)]
    [InlineData(2, MapDirection.Right, null)]
    public void EachArrowGoesToTheShapeNextToItThatWay(int from, MapDirection direction, int? expected)
    {
        IReadOnlyList<MapShape> shapes =
        [
            Box(0, 0, 0.6, 1),
            Box(0.6, 0, 0.4, 0.5),
            Box(0.6, 0.5, 0.4, 0.5),
        ];

        Assert.Equal(expected, MapNeighbours.Toward(shapes, from, direction, 800, 600));
    }

    /// <summary>
    /// A shape straight ahead beats a nearer one off to the side: the distance across the way counts
    /// for more than the gap along it.
    /// </summary>
    [Fact]
    public void AShapeInLineBeatsANearerOneBesideTheWay()
    {
        IReadOnlyList<MapShape> shapes =
        [
            Box(0, 0.45, 0.1, 0.1),
            Box(0.15, 0, 0.1, 0.1),
            Box(0.5, 0.45, 0.1, 0.1),
        ];

        Assert.Equal(2, MapNeighbours.Toward(shapes, 0, MapDirection.Right, 1000, 1000));
    }

    /// <summary>
    /// Measured in pixels, not fractions: on a canvas four times wider than it is high, a shape a
    /// quarter of the width away is further than one a quarter of the height away.
    /// </summary>
    [Fact]
    public void DistancesAreMeasuredInPixels()
    {
        IReadOnlyList<MapShape> shapes =
        [
            Box(0, 0, 0.1, 0.1),
            Box(0.3, 0.3, 0.1, 0.1),
            Box(0.15, 0.6, 0.1, 0.1),
        ];

        Assert.Equal(1, MapNeighbours.Toward(shapes, 0, MapDirection.Down, 100, 400));
        Assert.Equal(2, MapNeighbours.Toward(shapes, 0, MapDirection.Down, 4000, 100));
    }

    /// <summary>
    /// On a real drawing in every view, every move lands on a shape whose point is past the one it left,
    /// in the direction asked, and every shape at the top of the picture is reachable from the first.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void EveryMoveGoesThatWayAndEveryShapeCanBeReached(ExploreView view)
    {
        var tree = Tree();
        var drawing = ExploreSurface.Create(
            tree, tree.RootNode, view, 800, 600, 1, 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);
        var shapes = drawing.Shapes(null);
        var reached = new HashSet<int> { 0 };
        var waiting = new Queue<int>([0]);

        Assert.True(shapes.Count > 4, $"only {shapes.Count} shapes, too few to say anything");

        while (waiting.TryDequeue(out var from))
        {
            foreach (var direction in Enum.GetValues<MapDirection>())
            {
                if (MapNeighbours.Toward(shapes, from, direction, 800, 600) is not { } to)
                {
                    continue;
                }

                var (x, y) = (shapes[to].X - shapes[from].X, shapes[to].Y - shapes[from].Y);

                Assert.True(
                    direction switch
                    {
                        MapDirection.Left => x < 0,
                        MapDirection.Right => x > 0,
                        MapDirection.Up => y < 0,
                        _ => y > 0,
                    },
                    $"{direction} from {from} went to {to}, which is not that way");

                if (reached.Add(to))
                {
                    waiting.Enqueue(to);
                }
            }
        }

        Assert.Equal(shapes.Count, reached.Count);
    }

    private static MapShape Box(double x, double y, double width, double height) =>
        MapShape.Of(0, 1, new MapFrame(x, y, width, height));

    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 12).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: (12 - i) * (12 - i) * 1000L))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
