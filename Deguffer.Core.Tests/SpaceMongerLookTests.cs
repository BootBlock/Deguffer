using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// SpaceMonger's look, as measured from its own pixels: one black line between two boxes, a raised
/// bevel in its own shades of its eight colours, a folder's contents three pixels in under a band
/// twelve pixels deep, and each box named in its middle with as many lines as it has room for.
/// </summary>
public sealed class SpaceMongerLookTests
{
    private const int Width = 400;
    private const int Height = 300;

    private static readonly TileColour Black = new(0, 0, 0);

    /// <summary>Each of SpaceMonger's eight colours has its own two shades, and any other colour is mixed.</summary>
    [Fact]
    public void ABevelInSpaceMongersShadesUsesItsOwnForItsColours()
    {
        var classic = ShapeFinish.Of(MapPreset.SpaceMonger.Style, 1);
        var mixed = ShapeFinish.Of(MapPreset.SpaceMonger.Style with { BevelShades = MapBevelShades.Mixed }, 1);
        var cyan = TileColour.FromRgb(0x7FFFFF);
        var other = TileColour.FromRgb(0x336699);

        Assert.Equal(TileColour.FromRgb(0xDFFFFF), classic.Lit(cyan));
        Assert.Equal(TileColour.FromRgb(0x7FBFBF), classic.Shaded(cyan));
        Assert.Equal(TileColour.FromRgb(0x9F9FFF), classic.Shaded(TileColour.FromRgb(0xBFBFFF)));

        Assert.Equal(mixed.Lit(cyan), TileColour.FromRgb(0xBFFFFF));
        Assert.Equal(mixed.Lit(other), classic.Lit(other));
        Assert.Equal(mixed.Shaded(other), classic.Shaded(other));

        for (var depth = 0; depth < 8; depth++)
        {
            Assert.True(ClassicBevel.TryShade(DepthPalette.For(depth), out _, out _), $"no shades for level {depth}");
        }
    }

    /// <summary>
    /// Two boxes side by side share one black line: the left one's shaded edge, the line, and the right
    /// one's lit edge. Drawn each with its own line, there were two black pixels between them.
    /// </summary>
    [Fact]
    public void TwoBoxesSideBySideShareOneLine()
    {
        var drawing = Draw(MapPreset.SpaceMonger.Style);
        var pixels = Paint(drawing);
        var (left, right) = Neighbours(drawing);
        var row = (Math.Max(left.Top, right.Top) + Math.Min(left.Bottom, right.Bottom)) / 2;

        Assert.Equal(drawing.Finish.Shaded(left.Colour), ColourAt(pixels, left.Right - 1, row));
        Assert.Equal(Black, ColourAt(pixels, right.Left, row));
        Assert.Equal(drawing.Finish.Lit(right.Colour), ColourAt(pixels, right.Left + 1, row));

        var own = Draw(MapPreset.SpaceMonger.Style with { Lines = MapLines.Own });
        var ownPixels = Paint(own);
        var (ownLeft, ownRight) = Neighbours(own);
        var ownRow = (Math.Max(ownLeft.Top, ownRight.Top) + Math.Min(ownLeft.Bottom, ownRight.Bottom)) / 2;

        Assert.Equal(Black, ColourAt(ownPixels, ownLeft.Right - 1, ownRow));
        Assert.Equal(Black, ColourAt(ownPixels, ownRight.Left, ownRow));
    }

    /// <summary>
    /// What a folder holds is closed by one line along its right, then a pixel of the folder's colour,
    /// then the folder's shaded edge: three pixels in from its side, as on its left.
    /// </summary>
    [Fact]
    public void AFoldersContentsAreClosedByOneLineThreePixelsIn()
    {
        var drawing = Draw(MapPreset.SpaceMonger.Style);
        var pixels = Paint(drawing);
        var folder = drawing.Tiles.First(tile => tile.Header > 0 && tile.Node != ExploreTreeBuilder.RootNode);
        var quad = CushionQuad.Of(folder, drawing.Finish);
        var fill = drawing.MorphShapes(new HashSet<int> { folder.Node }).Single().Colour;
        var row = quad.Top + ((quad.Bottom - quad.Top) / 2);

        Assert.Equal(Black, ColourAt(pixels, quad.Left, row));
        Assert.Equal(drawing.Finish.Lit(fill), ColourAt(pixels, quad.Left + 1, row));
        Assert.Equal(fill, ColourAt(pixels, quad.Left + 2, row));
        Assert.Equal(Black, ColourAt(pixels, quad.Left + 3, row));

        Assert.Equal(Black, ColourAt(pixels, quad.Right - 3, row));
        Assert.Equal(fill, ColourAt(pixels, quad.Right - 2, row));
        Assert.Equal(drawing.Finish.Shaded(fill), ColourAt(pixels, quad.Right - 1, row));

        // The band: the line, the lit edge, then nine pixels for the name before what it holds.
        Assert.Equal(12, folder.Header);
    }

    /// <summary>A bevel is a raised button's: its top right corner shaded and its bottom left lit.</summary>
    [Fact]
    public void ABevelsCornersAreARaisedButtons()
    {
        var drawing = Draw(MapPreset.SpaceMonger.Style);
        var pixels = Paint(drawing);
        var (box, _) = Neighbours(drawing);

        Assert.Equal(drawing.Finish.Shaded(box.Colour), ColourAt(pixels, box.Right - 1, box.Top + 1));
        Assert.Equal(drawing.Finish.Lit(box.Colour), ColourAt(pixels, box.Left + 1, box.Bottom - 1));
    }

    /// <summary>
    /// A box is named in its middle, with as many of its lines as fit down it; a folder in its band, by
    /// its name alone.
    /// </summary>
    [Fact]
    public void ABoxIsNamedInItsMiddleWithTheLinesThatFit()
    {
        var drawing = Draw(MapPreset.SpaceMonger.Style);
        var insides = drawing.Labels.Where(label => label.Middle).ToList();

        Assert.NotEmpty(insides);
        Assert.All(insides, label =>
        {
            Assert.True(label.Centred);
            Assert.InRange(label.Lines, 1, ExploreLabel.MostLines);
            Assert.Equal(Math.Clamp((int)((label.Room.Bottom - label.Room.Top) / 11f), 1, 3), label.Lines);
        });

        Assert.All(drawing.Labels.Where(label => !label.Middle), label => Assert.Equal(1, label.Lines));
    }

    /// <summary>The classic lettering writes a name, a size in bytes and a date, one to a line, as many as there is room for.</summary>
    [Fact]
    public void AClassicCaptionIsANameASizeInBytesAndADate()
    {
        var written = new DateTime(2026, 11, 7, 16, 53, 10);
        var caption = new MapCaption("pagefile.sys", "50.0 GB", 53_687_091_200, written);
        var bytes = 53_687_091_200L.ToString("N0", CultureInfo.CurrentCulture);
        var date = written.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);

        Assert.Equal("pagefile.sys  50.0 GB", caption.Text(MapLettering.Standard, 3, MapDetail.Default));
        Assert.Equal("pagefile.sys", caption.Text(MapLettering.Classic, 1, MapDetail.Default));
        Assert.Equal($"pagefile.sys\n{bytes} bytes", caption.Text(MapLettering.Classic, 2, MapDetail.Default));
        Assert.Equal($"pagefile.sys\n{bytes} bytes\n{date}", caption.Text(MapLettering.Classic, 3, MapDetail.Default));
        Assert.Equal(string.Empty, MapCaption.None.Text(MapLettering.Classic, 3, MapDetail.Default));
    }

    private static TiledSurface Draw(MapStyle style) => (TiledSurface)ExploreSurface.Create(
        Tree(), ExploreTreeBuilder.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
        ShapeColours.ByDepth, MapLook.Default with { Style = style }, VolumeSpace.None);

    /// <summary>Two files side by side in one row, whole on the canvas, and the colour each is painted.</summary>
    private static ((int Left, int Top, int Right, int Bottom, TileColour Colour) Left, (int Left, int Top, int Right, int Bottom, TileColour Colour) Right) Neighbours(TiledSurface drawing)
    {
        var quads = drawing.Quads().Quads.ToArray().Where(quad => !quad.Framed && quad.Ridge > 0).ToList();

        foreach (var left in quads)
        {
            foreach (var right in quads)
            {
                if (right.Left == left.Right
                    && Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top) > 4
                    && left.Right - left.Left > 4
                    && right.Right - right.Left > 4)
                {
                    return ((left.Left, left.Top, left.Right, left.Bottom, left.Colour), (right.Left, right.Top, right.Right, right.Bottom, right.Colour));
                }
            }
        }

        throw new InvalidOperationException("No two boxes side by side.");
    }

    private static byte[] Paint(ExploreSurface drawing)
    {
        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];

        drawing.Painter(new TileColour(32, 32, 32)).Paint(pixels, new CanvasRegion(0, 0, Width, Height));

        return pixels;
    }

    private static TileColour ColourAt(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>A folder of four files of a few sizes beside two large files, so there are boxes side by side and a band.</summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder("Sample");
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("big.bin", IsDirectory: false, IsLink: false, Size: 3_000_000),
                new ExploreChild("large.bin", IsDirectory: false, IsLink: false, Size: 2_000_000),
            ]);

        builder.AddChildren(
            folder,
            [
                new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 1_500_000),
                new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 1_200_000),
                new ExploreChild("c.bin", IsDirectory: false, IsLink: false, Size: 900_000),
                new ExploreChild("d.bin", IsDirectory: false, IsLink: false, Size: 600_000),
            ]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
