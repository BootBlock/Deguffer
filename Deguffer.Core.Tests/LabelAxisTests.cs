using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A label's place as a rule (<see cref="LabelAxis"/>): on the canvas it was laid out for the rule gives
/// the place the drawing gave it, and a picture dragged across the screen carries the name by its point
/// to where the drawing at the new place puts it, unless its shape runs off the canvas.
/// </summary>
public sealed class LabelAxisTests
{
    private const int Width = 800;
    private const int Height = 600;

    /// <summary>A span kept on the canvas is cut to it, and a centred anchor is measured from the middle of what is left.</summary>
    [Fact]
    public void AKeptSpanIsCutToTheCanvasBeforeTheAnchorIsMeasured()
    {
        Assert.Equal(4, new LabelAxis(-50, 100, 4, Centred: false, KeptOnCanvas: true).Drawn(800));
        Assert.Equal(-46, new LabelAxis(-50, 100, 4, Centred: false, KeptOnCanvas: false).Drawn(800));
        Assert.Equal(745, new LabelAxis(700, 900, -5, Centred: true, KeptOnCanvas: true).Drawn(800));
        Assert.Equal(795, new LabelAxis(700, 900, -5, Centred: true, KeptOnCanvas: false).Drawn(800));
    }

    /// <summary>Every label's rule, on the canvas it was laid out for, gives the anchor the drawing gave it.</summary>
    [Theory]
    [InlineData(ExploreView.Treemap, 1, 0.5, 0.5)]
    [InlineData(ExploreView.Treemap, 3, 0.7, 0.2)]
    [InlineData(ExploreView.Treemap, 6, 0.1, 0.9)]
    [InlineData(ExploreView.Sunburst, 1, 0.5, 0.5)]
    public void EveryLabelsRuleGivesTheAnchorItWasDrawnAt(ExploreView view, double zoom, double x, double y)
    {
        var tree = Tree();
        var drawing = Draw(tree, view, MapViewport.Anchored(zoom, x, y, 0.5, 0.5, MapCeiling.Least));

        Assert.NotEmpty(drawing.Labels);
        Assert.All(drawing.Labels, label =>
        {
            var (anchorX, anchorY) = label.Anchor;

            Assert.Equal(anchorX, label.Across.Drawn(Width), 3);
            Assert.Equal(anchorY, label.Down.Drawn(Height), 3);
        });
    }

    /// <summary>
    /// The point a moving picture carries a name by is the start of its span, or the middle of a
    /// centred one, before any cut or inset: a point of the picture, which moves as the picture does.
    /// </summary>
    [Fact]
    public void ANameIsCarriedByTheStartOrMiddleOfItsSpan()
    {
        Assert.Equal(-50, new LabelAxis(-50, 100, 4, Centred: false, KeptOnCanvas: true).Carried);
        Assert.Equal(800, new LabelAxis(700, 900, -5, Centred: true, KeptOnCanvas: true).Carried);
    }

    /// <summary>
    /// Dragged across the screen, a name carried at the distance from its point its drawing put it at
    /// is where the drawing at the new place puts it, except a name of a shape that runs off the canvas
    /// at either place: that name goes with its shape until the drawing made where the picture stops
    /// puts it back at the edge.
    /// </summary>
    [Fact]
    public void ADraggedNameIsWhereTheNewDrawingPutsItUnlessItsShapeRunsOffTheCanvas()
    {
        var tree = Tree();
        var from = MapViewport.Anchored(1.5, 0.4, 0.45, 0.5, 0.5, MapCeiling.Least);
        var to = MapViewport.Anchored(1.5, 0.55, 0.55, 0.5, 0.5, MapCeiling.Least);
        var before = Draw(tree, ExploreView.Treemap, from);
        var after = Draw(tree, ExploreView.Treemap, to).Labels.ToDictionary(label => label.Node);

        var inside = 0;
        var cut = 0;

        foreach (var label in before.Labels)
        {
            if (!after.TryGetValue(label.Node, out var arrived) || arrived.Down.Centred != label.Down.Centred)
            {
                continue;
            }

            var shiftX = Moved(label.Across.Carried, from, to, Width) - label.Across.Carried;
            var shiftY = Moved(label.Down.Carried, from, to, Height) - label.Down.Carried;
            var carried = (X: label.Across.Drawn(Width) + shiftX, Y: label.Down.Drawn(Height) + shiftY);
            var (anchorX, anchorY) = arrived.Anchor;

            if (OnTheCanvas(label.Across, Width) && OnTheCanvas(arrived.Across, Width)
                && OnTheCanvas(label.Down, Height) && OnTheCanvas(arrived.Down, Height))
            {
                Assert.Equal(anchorX, carried.X, 1);
                Assert.Equal(anchorY, carried.Y, 1);
                inside++;
            }
            else if (Math.Abs(anchorX - carried.X) > 1 || Math.Abs(anchorY - carried.Y) > 1)
            {
                cut++;
            }
        }

        Assert.True(inside >= 2, $"only {inside} names were inside the canvas at both places");
        Assert.True(cut >= 1, "no name was on a shape running off the canvas, so nothing tested the cut");
    }

    /// <summary>Whether a kept span lies wholly on a canvas <paramref name="extent"/> long, so cutting it to the canvas changes nothing.</summary>
    private static bool OnTheCanvas(LabelAxis axis, int extent) =>
        !axis.KeptOnCanvas || (axis.From >= 0 && axis.To <= extent);

    /// <summary>Where a canvas point of a drawing at <paramref name="from"/> is on a drawing at <paramref name="to"/>.</summary>
    private static float Moved(float at, MapViewport from, MapViewport to, int extent)
    {
        var picture = (at / (extent * from.Zoom)) + (extent == Width ? from.Left : from.Top);

        return (float)((picture - (extent == Width ? to.Left : to.Top)) * extent * to.Zoom);
    }

    private static ExploreSurface Draw(ExploreTree tree, ExploreView view, MapViewport viewport) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, viewport);

    /// <summary>Folders inside folders, each holding files of a few sizes, so a zoom shows bands and names cut by the canvas's edges.</summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var tops = builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("Users", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("Games", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("Media", IsDirectory: true, IsLink: false, Size: 0),
        ]);

        for (var top = 0; top < 3; top++)
        {
            var inner = builder.AddChildren(tops + top,
            [
                new ExploreChild("a", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("b", IsDirectory: true, IsLink: false, Size: 0),
            ]);

            for (var folder = 0; folder < 2; folder++)
            {
                builder.AddChildren(inner + folder,
                [
                    new ExploreChild("one.bin", IsDirectory: false, IsLink: false, Size: 4_000_000 * (top + 1)),
                    new ExploreChild("two.bin", IsDirectory: false, IsLink: false, Size: 2_500_000 * (folder + 1)),
                    new ExploreChild("three.bin", IsDirectory: false, IsLink: false, Size: 1_500_000),
                ]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
