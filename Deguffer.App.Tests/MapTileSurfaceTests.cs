using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Windows.Graphics;

namespace Deguffer.App.Tests;

/// <summary>
/// A tile of a moving map is written whole into its surface, shaded on the GPU and painted on the CPU
/// alike, at every size a tile comes in: a whole square, and the smaller tiles of the picture's last
/// column and last row.
///
/// <para>Through the device the map draws through, on a compositor of its own, because what refused
/// the write was the compositor: the first write into a plain surface has to cover all of it. Each tile
/// in the last column or row was written into a whole square, and each was refused as an invalid
/// argument while the map was first drawn.</para>
/// </summary>
public sealed class MapTileSurfaceTests
{
    /// <summary>A map's canvas whose last column and last row of tiles at the first level are both short.</summary>
    private static readonly MapTiling Tiling = new(1516, 1051);

    private static readonly long LastColumn = (long)Math.Ceiling(Tiling.AcrossAt(0) / MapTiling.Size) - 1;

    private static readonly long LastRow = (long)Math.Ceiling(Tiling.DownAt(0) / MapTiling.Size) - 1;

    private static readonly TileColour Ground = new(32, 32, 32);

    public static TheoryData<long, long> Tiles => new()
    {
        { 0, 0 },
        { LastColumn, 0 },
        { 0, LastRow },
        { LastColumn, LastRow },
    };

    /// <summary>The canvas is one whose edge tiles are short, or the cases below test only whole squares.</summary>
    [Fact]
    public void TheEdgeTilesAreSmallerThanASquare()
    {
        Assert.InRange(Tiling.PixelsOf(Key(LastColumn, 0)).Width, 1, MapTiling.Size - 1);
        Assert.InRange(Tiling.PixelsOf(Key(0, LastRow)).Height, 1, MapTiling.Size - 1);
    }

    /// <summary>
    /// Shaded, and not skipped: <see cref="MapGraphics.Shade(CompositionDrawingSurface, CushionQuads, TileColour)"/>
    /// draws nothing on a device that cannot run the shaders, so the canvas it shaded is asked for too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tiles))]
    public void ATileIsShadedWholeIntoANewSurfaceOfItsSize(long column, long row)
    {
        var (width, height) = Tiling.PixelsOf(Key(column, row));

        var (size, shaded) = On(graphics =>
        {
            var surface = graphics.TileSurface(null, width, height);

            graphics.Shade(surface, Quads(width, height), Ground);

            return (surface.SizeInt32, graphics.ShrinkShaded(null, width, height, new SizeInt32(1, 1)) is not null);
        });

        Assert.True(shaded);
        Assert.Equal(new SizeInt32(width, height), size);
    }

    [Theory]
    [MemberData(nameof(Tiles))]
    public void ATileIsPaintedWholeIntoANewSurfaceOfItsSize(long column, long row)
    {
        var (width, height) = Tiling.PixelsOf(Key(column, row));

        var size = On(graphics =>
        {
            var surface = graphics.TileSurface(null, width, height);

            Paint(graphics, surface, width, height);

            return surface.SizeInt32;
        });

        Assert.Equal(new SizeInt32(width, height), size);
    }

    /// <summary>
    /// A surface let go of by one tile and written again for another is resized to the new one, from a
    /// whole square to a short tile and back, so neither write covers less or more than the surface.
    /// </summary>
    [Fact]
    public void ASpareSurfaceIsResizedToTheNextTile()
    {
        var keys = new[] { Key(0, 0), Key(LastColumn, LastRow), Key(0, LastRow), Key(0, 0) };

        var sizes = On(graphics =>
        {
            CompositionDrawingSurface? spare = null;
            var written = new List<SizeInt32>();

            foreach (var key in keys)
            {
                var (width, height) = Tiling.PixelsOf(key);

                spare = graphics.TileSurface(spare, width, height);
                graphics.Shade(spare, Quads(width, height), Ground);
                Paint(graphics, spare, width, height);

                written.Add(spare.SizeInt32);
            }

            return written;
        });

        Assert.Equal(keys.Select(key => Tiling.PixelsOf(key)).Select(size => new SizeInt32(size.Width, size.Height)), sizes);
    }

    private static MapTileKey Key(long column, long row) => new(0, column, row);

    /// <summary>A tile's worth of one shape, so the GPU has something to shade.</summary>
    private static CushionQuads Quads(int width, int height) =>
        new([new ExploreTile(Node: 1, Depth: 0, Bytes: 1, X: 0, Y: 0, Width: width, Height: height)], width, height, _ => new TileColour(200, 80, 40), ShapeFinish.Cushioned);

    private static void Paint(MapGraphics graphics, CompositionDrawingSurface surface, int width, int height) =>
        graphics.Write(surface, new byte[PixelBuffer.LengthFor(width, height)], width, new CanvasRegion(0, 0, width, height));

    /// <summary>Run <paramref name="work"/> on the compositor's thread, with the map's device, as the map does.</summary>
    private static T On<T>(Func<MapGraphics, T> work) =>
        CompositionThread.Run(() => work(MapGraphics.For(CompositionThread.Compositor)));
}
