using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The buffer every layout lays its shapes into. A layout's own tests rarely lay enough shapes to make
/// it grow, so its growth is proved here: every shape kept, in the order laid, however many times the
/// rented array is exchanged for a larger one.
/// </summary>
public sealed class ShapeBufferTests
{
    [Fact]
    public void EveryShapeIsKeptInOrderAcrossEachGrowth()
    {
        using var buffer = new ShapeBuffer();

        for (var i = 0; i < 5_000; i++)
        {
            buffer.Add(Shape(i));
        }

        var shapes = buffer.ToArray();

        Assert.Equal(5_000, shapes.Length);
        Assert.Equal(Enumerable.Range(0, 5_000), shapes.Select(shape => shape.Node));
        Assert.Equal(shapes, buffer.Shapes.ToArray());
    }

    [Fact]
    public void AClearedBufferLaysAgainFromTheStart()
    {
        using var buffer = new ShapeBuffer();

        for (var i = 0; i < 300; i++)
        {
            buffer.Add(Shape(i));
        }

        buffer.Clear();
        buffer.Add(Shape(7));

        Assert.Equal([Shape(7)], buffer.ToArray());
    }

    [Fact]
    public void AnEmptyBufferGivesAnEmptyArray()
    {
        using var buffer = new ShapeBuffer();

        Assert.Empty(buffer.ToArray());
        Assert.Equal(0, buffer.Count);
    }

    private static ExploreTile Shape(int node) =>
        new(node, Depth: node % 7, Bytes: node * 10L, X: node, Y: node / 2f, Width: 1, Height: 1);
}
