using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A map drawn in a <see cref="MapStyle"/>, pixel by pixel: flat shapes in their own colours, a bevel
/// lit along the top and the left, a sheen from top to bottom, a line inside each block and each folder
/// as the style asks for each, folders filled grey or tinted, bands and names left out. Every route a shape takes to the screen is held to it:
/// the treemap's quads, the tiles a zoom shows, the sunburst's sectors and the shapes a change of view
/// carries. That the GPU draws the same is <c>CushionRendererTests</c>'.
/// </summary>
public sealed class FinishedDrawingTests
{
    private const int Width = 480;
    private const int Height = 320;

    private static readonly TileColour Ground = new(32, 32, 32);

    private static readonly TileColour Black = new(0, 0, 0);

    private static readonly TileColour White = new(255, 255, 255);

    private static readonly ExploreTree Tree = Build();

    /// <summary>
    /// A flat map has no light in it: every pixel is the ground or exactly the colour of a shape drawn.
    /// A cushion would put hundreds of shades of each in between.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void AFlatMapIsTheShapesOwnColoursAndNothingElse(ExploreView view)
    {
        var flat = Drawn(view, MapStyle.Default with { Shading = MapShading.Flat });
        var colours = ColoursOf(flat);
        var seen = new HashSet<TileColour>();

        foreach (var colour in Pixels(Paint(flat)))
        {
            Assert.True(colour == Ground || colours.Contains(colour), $"{colour} is no shape's colour");
            seen.Add(colour);
        }

        Assert.True(seen.Count > 3, "too few colours drawn to say anything");
        Assert.Contains(Pixels(Paint(Drawn(view, MapStyle.Default))), colour => colour != Ground && !colours.Contains(colour));
    }

    /// <summary>
    /// A bevel is a pixel wide inside each shape: lit along the top and the left, shaded along the
    /// bottom and the right, and flat in the shape's own colour inside. A corner where a lit side meets
    /// a shaded one is shaded, as a raised button is drawn.
    /// </summary>
    [Fact]
    public void ABevelIsLitAlongTheTopAndLeftAndShadedAlongTheBottomAndRight()
    {
        var drawing = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Bevel });
        var pixels = Paint(drawing);
        var leaves = Leaves(drawing);

        Assert.NotEmpty(leaves);

        foreach (var (quad, colour) in leaves)
        {
            var (middleX, middleY) = ((quad.Left + quad.Right) / 2, (quad.Top + quad.Bottom) / 2);

            Assert.Equal(drawing.Finish.Lit(colour), ColourAt(pixels, quad.Left, middleY));
            Assert.Equal(drawing.Finish.Lit(colour), ColourAt(pixels, middleX, quad.Top));
            Assert.Equal(drawing.Finish.Shaded(colour), ColourAt(pixels, quad.Right - 1, middleY));
            Assert.Equal(drawing.Finish.Shaded(colour), ColourAt(pixels, middleX, quad.Bottom - 1));
            Assert.Equal(drawing.Finish.Shaded(colour), ColourAt(pixels, quad.Right - 1, quad.Top));
            Assert.Equal(colour, ColourAt(pixels, quad.Left + 1, middleY));
            Assert.Equal(colour, ColourAt(pixels, middleX, middleY));
        }
    }

    /// <summary>
    /// A line round every block, and none round a folder drawn round what it holds: the folder's band
    /// starts in its own colour. Asked the other way round, the folder has the line and the blocks none.
    /// </summary>
    [Fact]
    public void ABlocksLineAndAFoldersAreDrawnApart()
    {
        var blocks = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Flat, BlockBorders = MapBorder.Solid });
        var folders = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Flat, FolderBorders = MapBorder.Solid });
        var band = Banded(blocks);
        var bandRow = band.Quad.Top + 4;

        var blocksPainted = Paint(blocks);
        var foldersPainted = Paint(folders);

        Assert.NotEmpty(Leaves(blocks));

        foreach (var (quad, colour) in Leaves(blocks))
        {
            Assert.Equal(Black, ColourAt(blocksPainted, quad.Left, (quad.Top + quad.Bottom) / 2));
            Assert.Equal(colour, ColourAt(foldersPainted, quad.Left, (quad.Top + quad.Bottom) / 2));
        }

        Assert.Equal(band.Colour, ColourAt(blocksPainted, band.Quad.Left, bandRow));
        Assert.Equal(Black, ColourAt(foldersPainted, band.Quad.Left, bandRow));
        Assert.Equal(Black, ColourAt(foldersPainted, band.Quad.Left + 8, band.Quad.Top));
        Assert.Equal(band.Colour, ColourAt(foldersPainted, band.Quad.Left + 1, bandRow));
    }

    /// <summary>A line is the shape's own colour mixed towards its tone, by as much as its prominence says.</summary>
    [Fact]
    public void ALineIsTheShapesColourMixedTowardsItsTone()
    {
        var drawing = Drawn(
            ExploreView.Treemap,
            MapStyle.Default with { Shading = MapShading.Flat, BlockBorders = MapBorder.Medium, BorderTone = MapBorderTone.Light });
        var pixels = Paint(drawing);

        foreach (var (quad, colour) in Leaves(drawing))
        {
            Assert.Equal(ShapeFinish.Mix(colour, White, 128), ColourAt(pixels, quad.Left, (quad.Top + quad.Bottom) / 2));
        }
    }

    /// <summary>A thick line is two pixels at 100%, and a heavy one three, inside the shape's sides.</summary>
    [Theory]
    [InlineData(MapBorderWidth.Thick, 2)]
    [InlineData(MapBorderWidth.Heavy, 3)]
    public void AWideLineIsDrawnInsideTheShape(MapBorderWidth width, int pixels)
    {
        var drawing = Drawn(
            ExploreView.Treemap,
            MapStyle.Default with { Shading = MapShading.Flat, BlockBorders = MapBorder.Solid, BorderWidth = width });
        var painted = Paint(drawing);
        var leaves = Leaves(drawing);

        Assert.NotEmpty(leaves);

        foreach (var (quad, colour) in leaves)
        {
            var middleY = (quad.Top + quad.Bottom) / 2;

            Assert.Equal(Black, ColourAt(painted, quad.Left + pixels - 1, middleY));
            Assert.Equal(Black, ColourAt(painted, quad.Right - pixels, middleY));
            Assert.Equal(colour, ColourAt(painted, quad.Left + pixels, middleY));
            Assert.Equal(colour, ColourAt(painted, quad.Right - pixels - 1, middleY));
        }
    }

    /// <summary>
    /// The relief scales every cushion's rise, a flat style rises nowhere, and a bevel keeps the bevel
    /// of a folder with a band, which a cushion and a sheen leave flat behind the name.
    /// </summary>
    [Fact]
    public void TheReliefScalesTheRiseAndAFlatStyleRisesNowhere()
    {
        var standard = Drawn(ExploreView.Treemap, MapStyle.Default).Quads()!.Quads.ToArray();
        var strong = Drawn(ExploreView.Treemap, MapStyle.Default with { Relief = MapRelief.Strong }).Quads()!.Quads.ToArray();
        var deep = Drawn(ExploreView.Treemap, MapStyle.Default with { Relief = MapRelief.Deep }).Quads()!.Quads.ToArray();
        var flat = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Flat }).Quads()!.Quads.ToArray();
        var bevelled = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Bevel });

        Assert.Equal(standard.Length, strong.Length);
        Assert.Contains(standard, quad => quad.Ridge > 0);

        for (var i = 0; i < standard.Length; i++)
        {
            Assert.Equal(standard[i].Ridge * 1.5f, strong[i].Ridge, 5);
            Assert.Equal(standard[i].Ridge * 2.2f, deep[i].Ridge, 5);
        }

        Assert.All(flat, quad => Assert.Equal(0, quad.Ridge));
        Assert.Equal(0, Banded(Drawn(ExploreView.Treemap, MapStyle.Default)).Quad.Ridge);
        Assert.True(Banded(bevelled).Quad.Ridge > 0);
        Assert.Equal(0, Banded(Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Gradient })).Quad.Ridge);
    }

    /// <summary>
    /// A sheen lights each file from its top row down: brighter than its colour along the top, darker
    /// along the bottom, by <see cref="GradientShading.LightAt"/> at every row between, and the same all
    /// the way along each row. A folder with a band stays its own colour behind its name, which was
    /// chosen against that colour.
    /// </summary>
    [Theory]
    [InlineData(MapRelief.Subtle)]
    [InlineData(MapRelief.Standard)]
    [InlineData(MapRelief.Deep)]
    public void ASheenIsLighterAlongTheTopAndDarkerAlongTheBottom(MapRelief relief)
    {
        var drawing = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Gradient, Relief = relief });
        var pixels = Paint(drawing);
        var slope = GradientShading.SlopeAt(drawing.Finish.Relief);
        var leaves = Leaves(drawing);

        Assert.NotEmpty(leaves);

        foreach (var (quad, colour) in leaves)
        {
            for (var y = quad.Top; y < quad.Bottom; y++)
            {
                var lit = GradientShading.Lit(colour, GradientShading.LightAt(slope, y - quad.Top, quad.Bottom - quad.Top - 1));

                Assert.Equal(lit, ColourAt(pixels, quad.Left, y));
                Assert.Equal(lit, ColourAt(pixels, quad.Right - 1, y));
            }

            Assert.True(Brightness(ColourAt(pixels, quad.Left, quad.Top)) > Brightness(colour));
            Assert.True(Brightness(ColourAt(pixels, quad.Left, quad.Bottom - 1)) < Brightness(colour));
        }

        var band = Banded(drawing);

        Assert.Equal(band.Colour, ColourAt(pixels, band.Quad.Left + 8, band.Quad.Top + 1));
    }

    /// <summary>
    /// A sunburst's sheen runs along the radius: the disc in the middle is lighter than its colour at its
    /// centre and darker just inside its rim, the same at the top of the rim as at the bottom, and an
    /// aggregate stays flat, as it does under a cushion.
    /// </summary>
    [Fact]
    public void ASunburstsSheenRunsOutwardsFromTheMiddle()
    {
        var drawing = Drawn(ExploreView.Sunburst, MapStyle.Default with { Shading = MapShading.Gradient });
        var root = ExploreTreeBuilder.RootNode;
        var disc = drawing.MorphShapes(new HashSet<int> { root }).Single();
        var column = Width / 2;
        var pixels = Paint(drawing);
        var inside = Enumerable.Range(0, Height).Where(row => drawing.At(column + 0.5f, row + 0.5f)?.Node == root).ToList();
        var (top, bottom) = (inside[0], inside[^1]);

        Assert.True(Brightness(ColourAt(pixels, column, Height / 2)) > Brightness(disc.Colour));
        Assert.True(Brightness(ColourAt(pixels, column, top)) < Brightness(disc.Colour));
        Assert.Equal(ColourAt(pixels, column, top), ColourAt(pixels, column, bottom));
    }

    /// <summary>
    /// An aggregate is flat under a sheen, as it is under a cushion: it is not a thing on the disk. Drawn
    /// from a ring whose tail of small files is too narrow to draw one by one and wide enough together to
    /// fill a broad wedge.
    /// </summary>
    [Fact]
    public void ASunburstsAggregateIsFlatUnderASheen()
    {
        var builder = new ExploreTreeBuilder("Tail");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("big", IsDirectory: false, IsLink: false, Size: 1_000_000),
                .. Enumerable.Range(0, 400).Select(i => new ExploreChild($"small{i}", IsDirectory: false, IsLink: false, Size: 2_000)),
            ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var drawing = ExploreSurface.Create(
            tree, ExploreTreeBuilder.RootNode, ExploreView.Sunburst, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None,
            style: MapStyle.Default with { Shading = MapShading.Gradient });
        var pixels = Paint(drawing);
        var aggregated = 0;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (drawing.At(x + 0.5f, y + 0.5f)?.Node == ExploreTile.Aggregated)
                {
                    Assert.Equal(TilePalette.Aggregate, ColourAt(pixels, x, y));
                    aggregated++;
                }
            }
        }

        Assert.True(aggregated > 1000, $"only {aggregated} pixels of aggregate drawn, too few to say anything");
    }

    /// <summary>
    /// Without bands no folder keeps one, and no folder is named; with them, the same picture has both,
    /// or this proves nothing.
    /// </summary>
    [Fact]
    public void WithoutBandsNoFolderHasOneOrIsNamedInOne()
    {
        var banded = Drawn(ExploreView.Treemap, MapStyle.Default);
        var bare = Drawn(ExploreView.Treemap, MapStyle.Default with { FolderNames = false });

        Assert.Contains(banded.Tiles, tile => tile.Header > 0);
        Assert.Contains(banded.Labels, label => Tree.IsDirectory(label.Node));

        Assert.All(bare.Tiles, tile => Assert.Equal(0, tile.Header));
        Assert.DoesNotContain(bare.Labels, label => label.Node >= 0 && Tree.IsDirectory(label.Node));
        Assert.Contains(bare.Tiles, tile => tile.Framed);
    }

    /// <summary>
    /// Without names in the shapes, a treemap names only its folders, in their bands, and an icicle and
    /// a sunburst, which have no bands, name nothing.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    public void WithoutNamesInTheShapesOnlyTheBandsAreNamed(ExploreView view)
    {
        var named = Drawn(view, MapStyle.Default);
        var unnamed = Drawn(view, MapStyle.Default with { ShapeNames = false });
        var bands = unnamed.Tiles.Where(tile => tile.Header > 0).Select(tile => tile.Node).ToHashSet();

        Assert.Contains(named.Labels, label => !bands.Contains(label.Node));
        Assert.All(unnamed.Labels, label => Assert.Contains(label.Node, bands));
        Assert.Equal(view == ExploreView.Treemap, unnamed.Labels.Count > 0);
    }

    /// <summary>
    /// Grey frames fill every folder drawn round what it holds with one dark grey, and the name in its
    /// band is chosen against that grey. Nothing else changes colour.
    /// </summary>
    [Fact]
    public void GreyFramesFillEveryFolderDrawnRoundWhatItHolds()
    {
        var coloured = Drawn(ExploreView.Treemap, MapStyle.Default);
        var grey = Drawn(ExploreView.Treemap, MapStyle.Default with { FolderFill = MapFolderFill.Grey });
        var quads = coloured.Quads()!.Quads.ToArray();
        var greyQuads = grey.Quads()!.Quads.ToArray();

        Assert.Contains(greyQuads, quad => quad.Framed);

        for (var i = 0; i < quads.Length; i++)
        {
            Assert.Equal(quads[i].Framed ? TilePalette.FolderFrame : quads[i].Colour, greyQuads[i].Colour);
        }

        var band = grey.Labels.First(label => grey.Tiles.Any(tile => tile.Node == label.Node && tile.Header > 0));

        Assert.Equal(TilePalette.FolderFrame.ContrastingText, band.Colour);
    }

    /// <summary>
    /// Tinted frames fill every folder drawn round what it holds with its own colour mixed half way
    /// towards the frame grey, so the folders keep their hues apart from each other, and the name in its
    /// band is chosen against the tint. Nothing else changes colour.
    /// </summary>
    [Fact]
    public void TintedFramesMixEveryFolderHalfWayTowardsTheGrey()
    {
        var coloured = Drawn(ExploreView.Treemap, MapStyle.Default);
        var tinted = Drawn(ExploreView.Treemap, MapStyle.Default with { FolderFill = MapFolderFill.Tinted });
        var quads = coloured.Quads()!.Quads.ToArray();
        var tintedQuads = tinted.Quads()!.Quads.ToArray();

        Assert.True(tintedQuads.Where(quad => quad.Framed).Select(quad => quad.Colour).Distinct().Count() > 1, "every folder was tinted alike");

        for (var i = 0; i < quads.Length; i++)
        {
            Assert.Equal(quads[i].Framed ? ShapeFinish.Mix(quads[i].Colour, TilePalette.FolderFrame, 128) : quads[i].Colour, tintedQuads[i].Colour);
        }

        var band = tinted.Labels.First(label => tinted.Tiles.Any(tile => tile.Node == label.Node && tile.Header > 0));
        var folder = tinted.MorphShapes(new HashSet<int> { band.Node }).Single().Colour;

        Assert.Equal(ShapeFinish.Mix(coloured.MorphShapes(new HashSet<int> { band.Node }).Single().Colour, TilePalette.FolderFrame, 128), folder);
        Assert.Equal(folder.ContrastingText, band.Colour);
    }

    /// <summary>A high contrast theme's two colours stand in place of the style, grey or tinted frames and all.</summary>
    [Theory]
    [InlineData(MapFolderFill.Grey)]
    [InlineData(MapFolderFill.Tinted)]
    public void AHighContrastThemeReplacesTheStyle(MapFolderFill fill)
    {
        var window = new TileColour(0, 0, 0);
        var text = new TileColour(255, 255, 0);
        var drawing = Drawn(
            ExploreView.Treemap,
            MapPreset.SpaceMonger.Style with { FolderFill = fill },
            ShapeColours.Contrasted(new ContrastColours(window, text)));

        Assert.Equal(ShapeFinish.Contrasted(new ContrastColours(window, text), 1), drawing.Finish);
        Assert.All(drawing.Quads()!.Quads.ToArray(), quad => Assert.Equal(window, quad.Colour));
    }

    /// <summary>
    /// A shape a change of view carries keeps the line the picture draws round it: a block's where
    /// blocks have one, and none for a folder drawn round what it holds where folders have none.
    /// </summary>
    [Fact]
    public void AShapeAChangeOfViewCarriesKeepsItsLine()
    {
        var drawing = Drawn(ExploreView.Treemap, MapStyle.Default with { Shading = MapShading.Flat, BlockBorders = MapBorder.Solid });
        var shapes = drawing.MorphShapes(drawing.NodesDrawn.ToHashSet()).ToDictionary(shape => shape.Node);
        var framed = drawing.Tiles.Where(tile => tile.IsNode && tile.Framed).Select(tile => tile.Node).ToHashSet();

        Assert.NotEmpty(framed);
        Assert.All(shapes.Values, shape =>
            Assert.Equal(framed.Contains(shape.Node) ? null : new QuadEdges(Black, 1), shape.Edges));
    }

    /// <summary>A tile a zoom shows is finished as its drawing is, and knows its folders' frames.</summary>
    [Fact]
    public void ATileAZoomShowsIsFinishedAsItsDrawingIs()
    {
        var drawing = (TiledSurface)Drawn(ExploreView.Treemap, MapPreset.SpaceMonger.Style);
        var tiling = new MapTiling(Width, Height);
        var tile = drawing.Tile(tiling, tiling.At(1, 0.4, 0.6)!.Value);

        Assert.Equal(drawing.Finish, tile.Quads().Finish);
        Assert.Contains(tile.Quads().Quads.ToArray(), quad => quad.Framed);
    }

    /// <summary>
    /// A sunburst's bevel follows the light round the curve: the disc in the middle is lit just inside
    /// its rim at the top, and shaded just inside it at the bottom. A line round it, asked for, is drawn
    /// there instead.
    /// </summary>
    [Fact]
    public void ASunburstsBevelIsLitAboveAndShadedBelow()
    {
        var bevelled = Drawn(ExploreView.Sunburst, MapStyle.Default with { Shading = MapShading.Bevel });
        var lined = Drawn(ExploreView.Sunburst, MapStyle.Default with { Shading = MapShading.Flat, BlockBorders = MapBorder.Solid });
        var root = ExploreTreeBuilder.RootNode;
        var disc = bevelled.MorphShapes(new HashSet<int> { root }).Single();
        var (centreX, centreY) = (Width / 2f, Height / 2f);
        var radius = disc.Outline.Max(point => MathF.Sqrt(((point.X - centreX) * (point.X - centreX)) + ((point.Y - centreY) * (point.Y - centreY))));
        var column = (int)centreX;

        // The first and last rows down the column whose middles are inside the disc, which are a pixel
        // or less from its rim.
        var inside = Enumerable.Range(0, Height)
            .Where(row => MathF.Sqrt(MathF.Pow(column + 0.5f - centreX, 2) + MathF.Pow(row + 0.5f - centreY, 2)) < radius)
            .ToList();
        var (top, bottom) = (inside[0], inside[^1]);

        Assert.Equal(root, bevelled.At(column + 0.5f, top + 0.5f)?.Node);
        Assert.Equal(root, bevelled.At(column + 0.5f, bottom + 0.5f)?.Node);

        var pixels = Paint(bevelled);

        Assert.Equal(bevelled.Finish.Lit(disc.Colour), ColourAt(pixels, column, top));
        Assert.Equal(bevelled.Finish.Shaded(disc.Colour), ColourAt(pixels, column, bottom));
        Assert.Equal(disc.Colour, ColourAt(pixels, column, (int)centreY));
        Assert.Equal(Black, ColourAt(Paint(lined), column, top));
    }

    /// <summary>
    /// The rows of the map coloured by depth: every shape the colour of its level, the levels repeating
    /// every eight, and a shape's level counted from the folder on screen. Drawn inside a folder, so a
    /// level counted from the top of the scan would be one too many.
    /// </summary>
    [Fact]
    public void EveryShapeIsPaintedTheColourOfItsLevel()
    {
        var folder = Tree.ChildrenOf(ExploreTreeBuilder.RootNode).ToArray().First(node => Tree.IsDirectory(node));
        var drawing = Drawn(ExploreView.Treemap, MapStyle.Default, ShapeColours.ByDepth, folder);
        var quads = drawing.Quads()!.Quads.ToArray();
        var tiles = drawing.Tiles.Where(tile => CushionQuad.Of(tile, drawing.Finish).Reaches(Width, Height)).ToList();

        Assert.Equal(tiles.Count, quads.Length);
        Assert.Contains(tiles, tile => tile.Depth == 2);

        for (var i = 0; i < quads.Length; i++)
        {
            if (tiles[i].IsNode)
            {
                Assert.Equal(DepthPalette.For(tiles[i].Depth), quads[i].Colour);
                Assert.Equal(tiles[i].Depth, DepthPalette.LevelOf(Tree, tiles[i].Node, folder));
            }
        }

        Assert.Equal(DepthPalette.For(1), DepthPalette.For(9));
        Assert.Equal(8, DepthPalette.Bands.Count);
        Assert.Equal(["The folder shown", "1 level in", "2 levels in"], DepthPalette.Bands.Take(3).Select(band => band.Label));
    }

    /// <summary>A node outside the folder asked about has no level in it.</summary>
    [Fact]
    public void ANodeOutsideTheFolderHasNoLevelInIt()
    {
        var folder = Tree.ChildrenOf(ExploreTreeBuilder.RootNode).ToArray().First(node => Tree.IsDirectory(node));
        var outside = Tree.ChildrenOf(ExploreTreeBuilder.RootNode).ToArray().First(node => !Tree.IsDirectory(node));

        Assert.Null(DepthPalette.LevelOf(Tree, outside, folder));
        Assert.Equal(0, DepthPalette.LevelOf(Tree, folder, folder));
    }

    private static ExploreSurface Drawn(ExploreView view, MapStyle style, ShapeColours? colours = null, int root = ExploreTreeBuilder.RootNode) => ExploreSurface.Create(
        Tree, root, view, Width, Height, scale: 1, textScale: 1,
        colours ?? ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, style: style);

    /// <summary>
    /// The rectangles drawn as one block whole on the canvas and large enough to look inside, each with
    /// the colour it is painted.
    /// </summary>
    private static List<(CushionQuad Quad, TileColour Colour)> Leaves(ExploreSurface drawing) =>
    [
        .. drawing.Tiles
            .Where(tile => tile.IsNode && !tile.Framed)
            .Select(tile => (Quad: CushionQuad.Of(tile, drawing.Finish), tile.Node))
            .Where(leaf => leaf.Quad.Left >= 0 && leaf.Quad.Top >= 0 && leaf.Quad.Right <= Width && leaf.Quad.Bottom <= Height)
            .Where(leaf => leaf.Quad.Right - leaf.Quad.Left >= 8 && leaf.Quad.Bottom - leaf.Quad.Top >= 8)
            .Select(leaf => (leaf.Quad, drawing.MorphShapes(new HashSet<int> { leaf.Node }).Single().Colour)),
    ];

    /// <summary>A folder drawn with a band, whole on the canvas, and the colour it is painted.</summary>
    private static (CushionQuad Quad, TileColour Colour) Banded(ExploreSurface drawing)
    {
        var tile = drawing.Tiles.First(tile => tile.Header > 0 && tile.Node != ExploreTreeBuilder.RootNode && tile.X >= 0 && tile.Y >= 0);

        return (CushionQuad.Of(tile, drawing.Finish), drawing.MorphShapes(new HashSet<int> { tile.Node }).Single().Colour);
    }

    /// <summary>Every colour a shape of <paramref name="drawing"/> is painted, blocks included.</summary>
    private static HashSet<TileColour> ColoursOf(ExploreSurface drawing) =>
    [
        .. drawing.MorphShapes(drawing.NodesDrawn.ToHashSet()).Select(shape => shape.Colour),
        TilePalette.Aggregate,
    ];

    private static byte[] Paint(ExploreSurface drawing)
    {
        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];

        drawing.Painter(Ground).Paint(pixels, new CanvasRegion(0, 0, Width, Height));

        return pixels;
    }

    private static IEnumerable<TileColour> Pixels(byte[] pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            yield return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }
    }

    private static int Brightness(TileColour colour) => colour.Red + colour.Green + colour.Blue;

    private static TileColour ColourAt(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>
    /// A folder holding a folder and four files, beside three large files and a long tail of tiny ones:
    /// bands at two depths, leaves large enough to look inside, and an aggregate.
    /// </summary>
    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder("Sample");
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0),
                .. Enumerable.Range(0, 3).Select(i =>
                    new ExploreChild($"big{i}", IsDirectory: false, IsLink: false, Size: 1_000_000)),
                .. Enumerable.Range(0, 500).Select(i =>
                    new ExploreChild($"tiny{i}", IsDirectory: false, IsLink: false, Size: 1)),
            ]);

        var inner = builder.AddChildren(
            folder,
            [
                new ExploreChild("inner", IsDirectory: true, IsLink: false, Size: 0),
                .. Enumerable.Range(0, 4).Select(i =>
                    new ExploreChild($"leaf{i}", IsDirectory: false, IsLink: false, Size: 1_000_000)),
            ]);

        builder.AddChildren(
            inner,
            [.. Enumerable.Range(0, 2).Select(i =>
                new ExploreChild($"deep{i}", IsDirectory: false, IsLink: false, Size: 1_500_000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
