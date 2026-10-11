using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// In a high contrast theme the map is drawn in the theme's two colours and nothing else: every shape
/// and block flat in the window colour, its edges and every name in the text colour, with no cushion,
/// no grey and no light. Every route to the screen draws it so: the treemap's quads, the tiles a zoom
/// shows, the sunburst's sectors, and the shapes a change of view carries.
/// </summary>
public sealed class ContrastDrawingTests
{
    private const int Width = 480;
    private const int Height = 320;

    private static readonly TileColour Window = new(0, 0, 0);
    private static readonly TileColour Text = new(255, 255, 0);

    private static readonly ShapeColours Contrasted = ShapeColours.Contrasted(new ContrastColours(Window, Text));

    /// <summary>
    /// A volume, a folder of files, a long tail too small to draw and free space beside the root: every
    /// pixel is one of the two colours, and both show.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void EveryPixelIsOneOfTheThemesTwoColours(ExploreView view)
    {
        var drawing = Drawn(view);
        var pixels = Paint(drawing);
        var seen = new HashSet<TileColour>();

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var colour = new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);

            Assert.True(colour == Window || colour == Text, $"{colour} at pixel {offset / 4} is neither of the theme's colours");
            seen.Add(colour);
        }

        Assert.Equal(2, seen.Count);
    }

    /// <summary>
    /// The blocks a coloured map draws in greys of their own are the window colour too: the aggregate
    /// and the free space beside the volume. The fixture draws both, or this proves nothing.
    /// </summary>
    [Fact]
    public void EveryShapeAndBlockIsFilledWithTheWindowColour()
    {
        var quads = Drawn(ExploreView.Treemap).Quads()!.Quads.ToArray();

        Assert.Contains(quads, quad => quad.Colour == Window && quad.Left > 0);
        Assert.All(quads, quad => Assert.Equal(Window, quad.Colour));

        var tiles = Drawn(ExploreView.Treemap).Tiles;
        Assert.Contains(tiles, tile => tile.IsAggregate);
        Assert.Contains(tiles, tile => tile.IsFreeSpace);
    }

    /// <summary>
    /// Each shape is told from its neighbour by its edge: its pixels along each side are the text colour
    /// and the pixels inside them are the window colour.
    /// </summary>
    [Fact]
    public void EachShapeIsEdgedInTheTextColourAndFlatInside()
    {
        var drawing = Drawn(ExploreView.Treemap);
        var pixels = Paint(drawing);
        var leaves = drawing.Quads()!.Quads.ToArray()
            .Where(quad => quad.Left >= 0 && quad.Top >= 0 && quad.Right <= Width && quad.Bottom <= Height)
            .Where(quad => quad.Right - quad.Left >= 8 && quad.Bottom - quad.Top >= 8)
            .Where(quad => ColourAt(pixels, (quad.Left + quad.Right) / 2, (quad.Top + quad.Bottom) / 2) == Window)
            .ToList();

        Assert.NotEmpty(leaves);

        foreach (var quad in leaves)
        {
            var middleX = (quad.Left + quad.Right) / 2;
            var middleY = (quad.Top + quad.Bottom) / 2;

            Assert.Equal(Text, ColourAt(pixels, quad.Left, middleY));
            Assert.Equal(Text, ColourAt(pixels, quad.Right - 1, middleY));
            Assert.Equal(Text, ColourAt(pixels, middleX, quad.Top));
            Assert.Equal(Text, ColourAt(pixels, middleX, quad.Bottom - 1));
            Assert.Equal(Window, ColourAt(pixels, quad.Left + 1, middleY));
        }
    }

    /// <summary>
    /// A sector's rim is the text colour and its middle the window colour, so a ring of wedges reads as
    /// wedges with nothing but their edges to tell them apart.
    /// </summary>
    [Fact]
    public void EachSectorIsEdgedInTheTextColourAndFlatInside()
    {
        var drawing = Drawn(ExploreView.Sunburst);
        var pixels = Paint(drawing);
        var outlines = drawing.Outlines(drawing.NodesDrawn.ToHashSet());

        Assert.NotEmpty(outlines);

        var label = drawing.Labels.First(label => label.Node != ExploreTile.FreeSpace);
        var (x, y) = label.Anchor;

        Assert.Equal(Window, ColourAt(pixels, (int)x, (int)y + 2));
        Assert.Contains(outlines.SelectMany(outline => outline.Points), point =>
            point.X >= 1 && point.Y >= 1 && point.X < Width - 1 && point.Y < Height - 1
            && Neighbourhood(pixels, (int)point.X, (int)point.Y).Contains(Text));
    }

    /// <summary>
    /// A wedge with nothing outside it ends in an edge of its own: along the line from the middle of the
    /// disc through the wedge, the last pixel of the wedge is the text colour. The ground beyond it is
    /// the window colour, so that edge is all that shows where it ends.
    /// </summary>
    [Fact]
    public void AWedgeWithNothingOutsideItEndsInItsOwnEdge()
    {
        var drawing = Drawn(ExploreView.Sunburst);
        var pixels = Paint(drawing);
        var outermost = drawing.Shapes(null).Where(shape => shape.IsNode && drawing.Shapes(shape.Node).Count == 0).ToList();

        Assert.NotEmpty(outermost);

        foreach (var shape in outermost)
        {
            var (centreX, centreY) = (Width / 2.0, Height / 2.0);
            var (towardX, towardY) = ((shape.X * Width) - centreX, (shape.Y * Height) - centreY);
            var length = Math.Sqrt((towardX * towardX) + (towardY * towardY));
            (int X, int Y) last = default;

            for (var step = length; step < Width; step += 0.25)
            {
                var (x, y) = ((int)(centreX + (towardX / length * step)), (int)(centreY + (towardY / length * step)));

                if (x < 0 || y < 0 || x >= Width || y >= Height || drawing.At(x + 0.5f, y + 0.5f)?.Node != shape.Node)
                {
                    break;
                }

                last = (x, y);
            }

            Assert.Equal(Text, ColourAt(pixels, last.X, last.Y));
        }
    }

    /// <summary>
    /// Wedges side by side in a ring are told apart by their sides, and a ring from the ring inside it
    /// by its inner edge: walking from a wedge's point round its arc, and in towards the middle, the last
    /// pixel of the wedge each way is the text colour.
    /// </summary>
    [Fact]
    public void AWedgeIsEdgedAlongItsSidesAndItsInnerRing()
    {
        var drawing = Drawn(ExploreView.Sunburst);
        var pixels = Paint(drawing);
        var (centreX, centreY) = (Width / 2.0, Height / 2.0);
        var ring = drawing.Shapes(null).Where(shape => shape.IsNode).ToList();
        var outer = ring.Where(shape => drawing.Shapes(shape.Node).Count > 0).SelectMany(shape => drawing.Shapes(shape.Node)).Where(shape => shape.IsNode).ToList();

        Assert.True(ring.Count > 1, "one wedge has no neighbour to be told from");
        Assert.NotEmpty(outer);

        foreach (var shape in ring)
        {
            var (x, y) = ((shape.X * Width) - centreX, (shape.Y * Height) - centreY);
            var radius = Math.Sqrt((x * x) + (y * y));
            var angle = Math.Atan2(y, x);

            Assert.Equal(Text, LastOf(drawing, pixels, shape, step => (
                centreX + (radius * Math.Cos(angle - (step / radius))),
                centreY + (radius * Math.Sin(angle - (step / radius))))));
        }

        foreach (var shape in outer)
        {
            var (x, y) = ((shape.X * Width) - centreX, (shape.Y * Height) - centreY);
            var radius = Math.Sqrt((x * x) + (y * y));

            Assert.Equal(Text, LastOf(drawing, pixels, shape, step => (
                centreX + (x / radius * (radius - step)),
                centreY + (y / radius * (radius - step)))));
        }
    }

    /// <summary>
    /// The colour of the last pixel of <paramref name="shape"/> along the path <paramref name="along"/>
    /// takes from its point, a quarter of a pixel at a time.
    /// </summary>
    private static TileColour LastOf(ExploreSurface drawing, byte[] pixels, MapShape shape, Func<double, (double X, double Y)> along)
    {
        (int X, int Y) last = ((int)(shape.X * Width), (int)(shape.Y * Height));

        for (var step = 0.0; step < Width; step += 0.25)
        {
            var (x, y) = along(step);
            var (column, row) = ((int)x, (int)y);

            if (column < 0 || row < 0 || column >= Width || row >= Height || drawing.At(column + 0.5f, row + 0.5f)?.Node != shape.Node)
            {
                break;
            }

            last = (column, row);
        }

        return ColourAt(pixels, last.X, last.Y);
    }

    /// <summary>Every name is written in the theme's text colour, which every shape shares.</summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void EveryNameIsInTheTextColour(ExploreView view)
    {
        var labels = Drawn(view).Labels;

        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.Equal(Text, label.Colour));
    }

    /// <summary>
    /// The edge is a device pixel at 100% and two at 200%, and the reader's text size does not
    /// thicken it.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, 1)]
    [InlineData(2.0, 1.0, 2)]
    [InlineData(1.0, 2.0, 1)]
    [InlineData(1.5, 1.0, 2)]
    public void TheEdgesAreAsHeavyAsTheDisplaysScale(double scale, double textScale, int width)
    {
        var drawing = ExploreSurface.Create(
            Tree(), ExploreTreeBuilder.RootNode, ExploreView.Treemap, Width, Height, scale, textScale,
            Contrasted, ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Equal(new QuadEdges(Text, width), drawing.Quads()!.Edges);
    }

    /// <summary>A coloured map has no edges: its shapes are cushions.</summary>
    [Fact]
    public void AColouredMapHasNoEdges()
    {
        var drawing = ExploreSurface.Create(
            Tree(), ExploreTreeBuilder.RootNode, ExploreView.Treemap, Width, Height, 1, 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.Null(drawing.Quads()!.Edges);
        Assert.All(drawing.MorphShapes(drawing.NodesDrawn.ToHashSet()), shape => Assert.Null(shape.Edges));
    }

    /// <summary>A tile a zoom shows over the drawing is drawn in the same two colours, with the same edges.</summary>
    [Fact]
    public void ATileAZoomShowsIsDrawnAsItsDrawingIs()
    {
        var drawing = (TiledSurface)Drawn(ExploreView.Treemap);
        var tiling = new MapTiling(Width, Height);
        var tile = drawing.Tile(tiling, tiling.At(1, 0.4, 0.6)!.Value);

        Assert.Equal(drawing.Quads()!.Edges, tile.Quads().Edges);
        Assert.All(tile.Quads().Quads.ToArray(), quad => Assert.Equal(Window, quad.Colour));
    }

    /// <summary>
    /// A shape a change of view carries is the window colour with the edge the view draws it with, so
    /// what is seen travel is its edge.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Sunburst)]
    public void AShapeAChangeOfViewCarriesKeepsItsEdge(ExploreView view)
    {
        var drawing = Drawn(view);
        var shapes = drawing.MorphShapes(drawing.NodesDrawn.ToHashSet());

        Assert.NotEmpty(shapes);
        Assert.All(shapes, shape =>
        {
            Assert.Equal(Window, shape.Colour);
            Assert.Equal(new QuadEdges(Text, 1), shape.Edges);
        });
    }

    private static ExploreSurface Drawn(ExploreView view) => ExploreSurface.Create(
        Tree(), ExploreTreeBuilder.RootNode, view, Width, Height, scale: 1, textScale: 1,
        Contrasted, ExploreSpacing.Comfortable, new VolumeSpace(TotalBytes: 12_000_000, FreeBytes: 4_000_000));

    private static byte[] Paint(ExploreSurface drawing)
    {
        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];

        drawing.Painter(Window).Paint(pixels, new CanvasRegion(0, 0, Width, Height));

        return pixels;
    }

    private static TileColour ColourAt(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static HashSet<TileColour> Neighbourhood(byte[] pixels, int x, int y) =>
        [.. from dx in new[] { -1, 0, 1 } from dy in new[] { -1, 0, 1 } select ColourAt(pixels, x + dx, y + dy)];

    /// <summary>
    /// A folder of files beside a few large files and a long tail of tiny ones: nesting, siblings that
    /// share an edge, and an aggregate.
    /// </summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0),
                .. Enumerable.Range(0, 3).Select(i =>
                    new ExploreChild($"big{i}", IsDirectory: false, IsLink: false, Size: 1_000_000)),
                .. Enumerable.Range(0, 500).Select(i =>
                    new ExploreChild($"tiny{i}", IsDirectory: false, IsLink: false, Size: 1)),
            ]);

        builder.AddChildren(
            folder,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"leaf{i}", IsDirectory: false, IsLink: false, Size: 1_000_000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
