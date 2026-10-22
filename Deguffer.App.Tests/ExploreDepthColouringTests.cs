using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the map coloured by depth: its legend, and the line under the map, which
/// says how many levels in the shape under the pointer is, so the colour is never the only thing saying
/// it (§6.5). What colour each level is painted is asserted in Core by <c>FinishedDrawingTests</c>.
/// </summary>
public sealed class ExploreDepthColouringTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    [Fact]
    public void TheLineUnderTheMapSaysHowManyLevelsInAShapeIs() => UiThread.Run(async () =>
    {
        var root = _explore.Temp.CreateDirectory("scan");
        var builder = new ExploreTreeBuilder(root);
        var outer = builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("Outer"), ExploreFixture.File("top.txt", 10)]);
        var inner = builder.AddChildren(outer, [ExploreFixture.Folder("Inner")]);

        builder.AddChildren(inner, [ExploreFixture.File("deep.bin", 4_000)]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var page = _explore.Page();

        page.ScopeTo(root);
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        page.SelectedColouring = ExploreColouring.Depth;

        Assert.True(page.ShowsLegend);
        Assert.Equal(DepthPalette.Bands.Select(band => band.Label), page.Legend.Select(band => band.Label));

        page.Hover(new ExploreHit(Find(tree, "top.txt"), 10));
        Assert.EndsWith(", 1 level in", page.HoveredFigures);

        page.Hover(new ExploreHit(Find(tree, "deep.bin"), 4_000));
        Assert.EndsWith(", 3 levels in", page.HoveredFigures);

        page.SelectedColouring = ExploreColouring.Branch;
        page.Hover(new ExploreHit(Find(tree, "deep.bin"), 4_000));
        Assert.DoesNotContain("level", page.HoveredFigures);
    });

    private static int Find(ExploreTree tree, string name)
    {
        var waiting = new Stack<int>([tree.RootNode]);

        while (waiting.TryPop(out var node))
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                waiting.Push(child);
            }
        }

        throw new InvalidOperationException($"{name} is not in the tree.");
    }
}
