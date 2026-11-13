using System.Numerics;
using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// How the marks over the map are drawn again: the outline round what the pointer is over is asked for
/// again whenever a drawing settles or the tiles under a still pointer change, and is built only where
/// what it goes round changed. Each build is a new geometry, a new path and a new mask for the lift.
/// </summary>
public sealed class ExploreHighlightTests
{
    private static readonly Vector2 Canvas = new(300, 100);

    /// <summary>The same shape asked for again, in a new list as a drawing gives it, leaves every path as it was.</summary>
    [Fact]
    public void TheSameShapeHoveredAgainIsNotDrawnAgain()
    {
        var (before, again, other) = CompositionThread.Run(() =>
        {
            var highlight = Highlight();

            highlight.ShowHovered([Square(1, 0)], null, Canvas);
            var before = Paths(highlight);

            highlight.ShowHovered([Square(1, 0)], null, Canvas);
            var again = Paths(highlight);

            highlight.ShowHovered([Square(2, 100)], null, Canvas);

            return (before, again, Paths(highlight));
        });

        Assert.Equal(before, again, ReferenceEqualityComparer.Instance);
        Assert.NotEqual(before, other, ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// The effects turned off draw the same shape again, because the lift goes with them, though nothing
    /// about the shape changed.
    /// </summary>
    [Fact]
    public void TheSameShapeIsDrawnAgainWhenTheEffectsChange()
    {
        var (before, after) = CompositionThread.Run(() =>
        {
            var highlight = Highlight();

            highlight.ShowHovered([Square(1, 0)], null, Canvas);
            var before = Paths(highlight);

            highlight.Restyle(Color.FromArgb(255, 0, 120, 215), system: null, effectsFast: false);

            return (before, Paths(highlight));
        });

        Assert.NotEqual(before, after, ReferenceEqualityComparer.Instance);
    }

    private static ExploreHighlight Highlight()
    {
        var compositor = CompositionThread.Compositor;

        return new ExploreHighlight(compositor, MapGraphics.For(compositor), new MapCamera(compositor));
    }

    /// <summary>A square 100 pixels across, of <paramref name="node"/>, <paramref name="left"/> pixels from the canvas's left.</summary>
    private static ExploreOutline Square(int node, float left) =>
        new(node, [new(left, 0), new(left + 100, 0), new(left + 100, 100), new(left, 100)]);

    /// <summary>The path of every line drawn over the picture, in the order they are drawn.</summary>
    private static List<CompositionPath> Paths(ExploreHighlight highlight)
    {
        var paths = new List<CompositionPath>();

        foreach (var shapes in ((ContainerVisual)highlight.Root).Children.OfType<ShapeVisual>())
        {
            foreach (var container in shapes.Shapes.OfType<CompositionContainerShape>())
            {
                paths.AddRange(container.Shapes
                    .OfType<CompositionSpriteShape>()
                    .Select(sprite => ((CompositionPathGeometry)sprite.Geometry).Path));
            }
        }

        Assert.NotEmpty(paths);

        return paths;
    }
}
