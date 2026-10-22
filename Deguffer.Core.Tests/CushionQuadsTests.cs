using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The quads both routes to the screen shade: the GPU draws them as they stand, and the CPU paints
/// them, so what is decided here is the picture on either.
/// </summary>
public sealed class CushionQuadsTests
{
    private static readonly TileColour Red = new(200, 40, 40);

    /// <summary>
    /// A shape's edges are rounded to the nearest pixel, so two shapes that share an edge share it on
    /// the canvas, with no gap and no overlap between them.
    /// </summary>
    [Fact]
    public void EdgesAreRoundedSoNeighboursMeet()
    {
        var quads = Quads(
            new ExploreTile(Node: 1, Depth: 1, Bytes: 1, X: 0.4f, Y: 0.6f, Width: 10.2f, Height: 5),
            new ExploreTile(Node: 2, Depth: 1, Bytes: 1, X: 10.6f, Y: 0.6f, Width: 9, Height: 5));

        Assert.Equal((0, 1, 11, 6), Edges(quads[0]));
        Assert.Equal((11, 1, 20, 6), Edges(quads[1]));
    }

    /// <summary>
    /// A file's cushion rises by its depth's ridge, and the shapes drawn flat rise not at all: a block
    /// that is not a node, and a folder whose name is written across its band.
    /// </summary>
    [Fact]
    public void OnlyANodeWithoutABandHasACushion()
    {
        var quads = Quads(
            new ExploreTile(Node: 1, Depth: 3, Bytes: 1, X: 0, Y: 0, Width: 10, Height: 10),
            new ExploreTile(Node: 2, Depth: 3, Bytes: 1, X: 10, Y: 0, Width: 10, Height: 10, Header: 4),
            new ExploreTile(Node: ExploreTile.Aggregated, Depth: 3, Bytes: 1, X: 20, Y: 0, Width: 10, Height: 10));

        Assert.Equal((float)CushionShading.RidgeAt(3), quads[0].Ridge);
        Assert.Equal(0, quads[1].Ridge);
        Assert.Equal(0, quads[2].Ridge);
    }

    /// <summary>
    /// A shape that covers no pixel of the canvas is left out, whether it lies off the canvas or is too
    /// thin to cover a pixel once rounded, and the rest keep the order they are drawn in.
    /// </summary>
    [Fact]
    public void AShapeThatCoversNoPixelIsLeftOutAndTheRestKeepTheirOrder()
    {
        var quads = Quads(
            new ExploreTile(Node: 1, Depth: 0, Bytes: 1, X: 0, Y: 0, Width: 100, Height: 100),
            new ExploreTile(Node: 2, Depth: 1, Bytes: 1, X: -50, Y: 0, Width: 50, Height: 10),
            new ExploreTile(Node: 3, Depth: 1, Bytes: 1, X: 5, Y: 5, Width: 0.3f, Height: 10),
            new ExploreTile(Node: 4, Depth: 1, Bytes: 1, X: 100, Y: 0, Width: 20, Height: 10),
            new ExploreTile(Node: 5, Depth: 1, Bytes: 1, X: -20, Y: 90, Width: 30, Height: 30));

        Assert.Equal([(0, 0, 100, 100), (-20, 90, 10, 120)], quads.Select(Edges));
    }

    /// <summary>Each quad is the colour its node is painted at its depth.</summary>
    [Fact]
    public void EachQuadIsItsNodesColourAtItsDepth()
    {
        var quads = new CushionQuads(
            [
                new ExploreTile(Node: 7, Depth: 2, Bytes: 1, X: 0, Y: 0, Width: 10, Height: 10),
                new ExploreTile(Node: 9, Depth: 5, Bytes: 1, X: 10, Y: 0, Width: 10, Height: 10),
            ],
            100,
            100,
            tile => new TileColour((byte)tile.Node, (byte)tile.Depth, 0),
            ShapeFinish.Cushioned).Quads.ToArray();

        Assert.Equal([new TileColour(7, 2, 0), new TileColour(9, 5, 0)], quads.Select(quad => quad.Colour));
    }

    /// <summary>
    /// The GPU reads an array of quads as it stands, as four integers, a float, three bytes of colour and
    /// a byte saying whether the quad is a folder's frame, so the layout is part of what a quad is. A
    /// field moved or widened shades garbage, with no error.
    /// </summary>
    [Fact]
    public void AQuadIsLaidOutAsTheGpuReadsIt()
    {
        Assert.Equal(24, Unsafe.SizeOf<CushionQuad>());
        Assert.Equal(24, Marshal.SizeOf<CushionQuad>());

        var quad = new CushionQuad(1, 2, 3, 4, 0.5f, new TileColour(5, 6, 7), Framed: true);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<CushionQuad>(in quad));

        Assert.Equal([1, 2, 3, 4], MemoryMarshal.Cast<byte, int>(bytes[..16]).ToArray());
        Assert.Equal(0.5f, MemoryMarshal.Read<float>(bytes[16..20]));
        Assert.Equal([5, 6, 7, 1], bytes[20..24].ToArray());
    }

    private static CushionQuad[] Quads(params ExploreTile[] tiles) =>
        new CushionQuads(tiles, 100, 100, _ => Red, ShapeFinish.Cushioned).Quads.ToArray();

    private static (int Left, int Top, int Right, int Bottom) Edges(CushionQuad quad) =>
        (quad.Left, quad.Top, quad.Right, quad.Bottom);
}
