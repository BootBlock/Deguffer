using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;

namespace Deguffer.App.Tests;

/// <summary>
/// The GPU shades the same picture the CPU paints: the same shapes, the same colours and the same
/// edges. The CPU's rasteriser is the reference, and the map falls back to it wherever the GPU is not
/// used, so a picture that differed would change as the route did.
///
/// <para>Shaded on WARP, Direct3D's software device, so the test needs no GPU and sees the same
/// result on any machine. The shader is the same on every device.</para>
///
/// <para>The tolerance: every channel of every pixel within one level of the CPU's, and fewer than one
/// pixel in a thousand differing at all. A float against a double can put the last bit of a product
/// either side of a whole level, and on a real volume that is a few dozen pixels of a canvas. Any
/// difference in the edges, the colours or the light is far more than that.</para>
/// </summary>
public sealed class CushionRendererTests : IDisposable
{
    private const int Width = 777;
    private const int Height = 555;

    private static readonly TileColour Ground = new(32, 32, 32);

    private readonly CanvasDevice _device = new(forceSoftwareRenderer: true);

    private readonly CushionRenderer _renderer;

    public CushionRendererTests() =>
        _renderer = CushionRenderer.For(_device) ?? throw new InvalidOperationException("WARP cannot run the cushion shaders.");

    public void Dispose()
    {
        _renderer.Dispose();
        _device.Dispose();
    }

    /// <summary>A whole tree of nested folders, flat bands and files at many depths, shaded as it is painted.</summary>
    [Fact]
    public void AWholeTreeIsShadedAsItIsPainted() => AssertShadedAsPainted(Picture(MapViewport.Whole));

    /// <summary>
    /// Zoomed in, most shapes run off the canvas, and a cushion is measured across its whole shape and
    /// not across the part on the canvas.
    /// </summary>
    [Fact]
    public void AZoomedPictureIsShadedAsItIsPainted()
    {
        var picture = Picture(MapViewport.Seen(zoom: 2.5, left: 0.31, top: 0.42));
        var quads = picture.Quads()!.Quads.ToArray();

        Assert.Contains(quads, quad => quad.Left < 0 || quad.Top < 0 || quad.Right > Width || quad.Bottom > Height);

        AssertShadedAsPainted(picture);
    }

    /// <summary>
    /// The renderer keeps one texture for every size, so a smaller canvas after a larger one is shaded
    /// at its own size, with nothing of the larger one in it.
    /// </summary>
    [Fact]
    public void ASmallerCanvasAfterALargerIsShadedAtItsOwnSize()
    {
        var larger = Picture(MapViewport.Whole, Width * 2, Height * 2).Quads()!;

        _renderer.Shade(larger, new TileColour(250, 0, 250), Whole(larger), light: null);

        AssertShadedAsPainted(Picture(MapViewport.Whole, 300, 200));
    }

    /// <summary>
    /// WARP runs on the CPU, and the renderer says so: the map paints on the CPU for a software device,
    /// a region at a time and off the UI thread, rather than shade on it on the UI thread.
    /// </summary>
    [Fact]
    public void ARendererOnWarpSaysItRunsOnSoftware() => Assert.True(_renderer.OnSoftware);

    /// <summary>
    /// The pointer's light brightens the cushions round it and darkens none, and past its reach the
    /// picture is the one shaded without it, pixel for pixel: a light that moves on leaves nothing
    /// behind.
    /// </summary>
    [Fact]
    public void ThePointerLightBrightensWithinItsReachAndChangesNothingPastIt()
    {
        var quads = Picture(MapViewport.Whole).Quads()!;
        var light = new PointerLight(400, 300, 120);
        var unlit = Shaded(quads);
        var lit = _renderer.Shade(quads, Ground, Whole(quads), light).GetPixelBytes(0, 0, quads.Width, quads.Height);
        var brighter = 0;

        for (var i = 0; i < unlit.Length; i += 4)
        {
            var x = (i / 4 % quads.Width) + 0.5f - light.X;
            var y = (i / 4 / quads.Width) + 0.5f - light.Y;
            var within = (x * x) + (y * y) < light.Reach * light.Reach;

            for (var channel = 0; channel < 3; channel++)
            {
                if (within)
                {
                    Assert.True(lit[i + channel] >= unlit[i + channel], $"the light darkened pixel {i / 4}");
                }
                else
                {
                    Assert.Equal(unlit[i + channel], lit[i + channel]);
                }
            }

            brighter += within && lit[i + 1] > unlit[i + 1] + 8 ? 1 : 0;
        }

        Assert.True(brighter > 1000, $"only {brighter} pixels were lit");
    }

    /// <summary>
    /// A region shaded on its own, as the light shades where it moves, is the same picture as the same
    /// region of the whole canvas: the cushions are measured across their whole shapes, not across the
    /// part in the region.
    /// </summary>
    [Fact]
    public void ARegionShadedOnItsOwnIsThePictureOfTheWhole()
    {
        var quads = Picture(MapViewport.Whole).Quads()!;
        var light = new PointerLight(400, 300, 120);
        var region = new CanvasRegion(260, 170, 290, 250);
        var whole = _renderer.Shade(quads, Ground, Whole(quads), light).GetPixelBytes(region.X, region.Y, region.Width, region.Height);
        var part = _renderer.Shade(quads, Ground, region, light).GetPixelBytes(region.X, region.Y, region.Width, region.Height);

        Assert.Equal(whole, part);
    }

    /// <summary>
    /// In a high contrast theme's colours the GPU draws what the CPU paints exactly: flat shapes in the
    /// window colour with their edges in the text colour, whole and zoomed, with no light in them even
    /// where the pointer's light is asked for.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(2.5, 0.31, 0.42)]
    public void AHighContrastPictureIsShadedExactlyAsItIsPainted(double zoom, double left, double top)
    {
        var window = new TileColour(0, 0, 0);
        var text = new TileColour(255, 255, 0);
        var picture = Picture(MapViewport.Seen(zoom, left, top), colours: ShapeColours.Contrasted(new ContrastColours(window, text)));
        var quads = picture.Quads()!;
        var painted = new byte[PixelBuffer.LengthFor(picture.Width, picture.Height)];

        picture.Painter(window).Paint(painted, new CanvasRegion(0, 0, picture.Width, picture.Height));

        var shaded = _renderer.Shade(quads, window, Whole(quads), new PointerLight(400, 300, 120))
            .GetPixelBytes(0, 0, quads.Width, quads.Height);

        Assert.True(quads.Quads.Length > 100, $"only {quads.Quads.Length} shapes drawn, too few to say anything");
        Assert.Contains(painted.Chunk(4), pixel => pixel[2] == text.Red && pixel[0] == text.Blue);

        for (var i = 0; i < painted.Length; i += 4)
        {
            Assert.True(
                painted.AsSpan(i, 4).SequenceEqual(shaded.AsSpan(i, 4)),
                $"pixel ({i / 4 % picture.Width}, {i / 4 / picture.Width}) is {Colour(shaded, i)} shaded and {Colour(painted, i)} painted");
        }
    }

    /// <summary>
    /// Every finish the style can ask for is shaded as it is painted: flat, bevelled, lined round each
    /// block or each folder, light or dark, thin or thick, folders filled grey, and cushions at every
    /// depth with a line round them. A finish with no cushion in it is whole numbers on both routes,
    /// so it is the same picture exactly; one with a cushion is held to the cushions' tolerance.
    /// </summary>
    [Theory]
    [MemberData(nameof(Finishes))]
    public void EveryFinishIsShadedAsItIsPainted(string name, MapStyle style)
    {
        var picture = Picture(MapViewport.Seen(zoom: 1.6, left: 0.2, top: 0.3), style: style);
        var finish = picture.Quads()!.Finish;

        Assert.True(finish.Borders.Width > 0 || finish.Bevel.Width > 0 || style.FolderFill == MapFolderFill.Grey, $"{name} draws nothing a cushion does not");

        if (style.Shading == MapShading.Cushion)
        {
            AssertShadedAsPainted(picture);
        }
        else
        {
            AssertShadedExactlyAsPainted(picture);
        }
    }

    /// <summary>The finishes <see cref="EveryFinishIsShadedAsItIsPainted"/> asks about, each by name.</summary>
    public static TheoryData<string, MapStyle> Finishes => new()
    {
        { "flat, faint blocks, solid folders", new MapStyle(MapShading.Flat, BlockBorders: MapBorder.Faint, FolderBorders: MapBorder.Solid) },
        { "bevel, no lines", new MapStyle(MapShading.Bevel) },
        { "strong bevel, thick light lines", new MapStyle(MapShading.Bevel, MapRelief.Strong, MapBorder.Strong, MapBorder.Medium, MapBorderWidth.Thick, MapBorderTone.Light) },
        { "SpaceMonger", MapPreset.SpaceMonger.Style },
        { "cushions with grey frames", MapPreset.WizTree.Style },
        { "cushions with lines", new MapStyle(MapShading.Cushion, MapRelief.Subtle, MapBorder.Medium, MapBorder.Strong) },
        { "flat without bands or names", MapPreset.Outlined.Style with { FolderNames = false } },
    };

    /// <summary>
    /// On a flat picture the pointer's light brightens what is round it and leaves the rest as it was,
    /// pixel for pixel. A line takes none of it: within the light, the inside of each file brightens and
    /// the line round it does not. The lines are a mid tone, which a light could brighten, so a line left
    /// alone is the shader's choice and not black that no light can change.
    /// </summary>
    [Fact]
    public void ThePointerLightBrightensAFlatPictureWithinItsReachButNotItsLines()
    {
        var picture = Picture(MapViewport.Whole, style: new MapStyle(MapShading.Flat, BlockBorders: MapBorder.Medium, BorderWidth: MapBorderWidth.Thick));
        var quads = picture.Quads()!;
        var light = new PointerLight(400, 300, 160);
        var unlit = Shaded(quads);
        var lit = _renderer.Shade(quads, Ground, Whole(quads), light).GetPixelBytes(0, 0, quads.Width, quads.Height);
        var brighter = 0;

        for (var i = 0; i < unlit.Length; i += 4)
        {
            var x = (i / 4 % quads.Width) + 0.5f - light.X;
            var y = (i / 4 / quads.Width) + 0.5f - light.Y;
            var within = (x * x) + (y * y) < light.Reach * light.Reach;

            for (var channel = 0; channel < 3; channel++)
            {
                if (within)
                {
                    Assert.True(lit[i + channel] >= unlit[i + channel], $"the light darkened pixel {i / 4}");
                }
                else
                {
                    Assert.Equal(unlit[i + channel], lit[i + channel]);
                }
            }

            brighter += within && lit[i + 1] > unlit[i + 1] + 8 ? 1 : 0;
        }

        Assert.True(brighter > 1000, $"only {brighter} pixels were lit");

        // Files whose middle the light brightened: the line along their left side is as it was.
        var files = picture.Tiles
            .Where(tile => tile.IsNode && !tile.Framed && tile.Width >= 12 && tile.Height >= 12)
            .Select(tile => CushionQuad.Of(tile, quads.Finish))
            .Where(quad => quad.Left >= 0 && quad.Top >= 0 && quad.Right <= quads.Width && quad.Bottom <= quads.Height)
            .Select(quad => (Quad: quad, Middle: Offset(quads, (quad.Left + quad.Right) / 2, (quad.Top + quad.Bottom) / 2)))
            .Where(file => lit[file.Middle + 1] > unlit[file.Middle + 1] + 8)
            .ToList();

        Assert.NotEmpty(files);

        foreach (var (quad, _) in files)
        {
            var edge = Offset(quads, quad.Left, (quad.Top + quad.Bottom) / 2);

            Assert.True(lit.AsSpan(edge, 3).SequenceEqual(unlit.AsSpan(edge, 3)), $"the light reached the line at ({quad.Left}, {(quad.Top + quad.Bottom) / 2})");
        }
    }

    private static int Offset(CushionQuads quads, int x, int y) => ((y * quads.Width) + x) * 4;

    /// <summary>A canvas with no shapes on it is the ground alone.</summary>
    [Fact]
    public void ACanvasWithNoShapesIsTheGround()
    {
        var shaded = Shaded(new CushionQuads([], 64, 48, _ => default, ShapeFinish.Cushioned));

        for (var i = 0; i < shaded.Length; i += 4)
        {
            Assert.Equal((Ground.Blue, Ground.Green, Ground.Red, (byte)255), (shaded[i], shaded[i + 1], shaded[i + 2], shaded[i + 3]));
        }
    }

    private void AssertShadedAsPainted(ExploreSurface picture)
    {
        var quads = picture.Quads()!;
        var painted = new byte[PixelBuffer.LengthFor(picture.Width, picture.Height)];

        picture.Painter(Ground).Paint(painted, new CanvasRegion(0, 0, picture.Width, picture.Height));

        var shaded = Shaded(quads);
        var differing = 0;

        Assert.True(quads.Quads.Length > 100, $"only {quads.Quads.Length} shapes drawn, too few to say anything");
        Assert.Equal(painted.Length, shaded.Length);

        for (var i = 0; i < painted.Length; i += 4)
        {
            var most = 0;

            for (var channel = 0; channel < 3; channel++)
            {
                most = Math.Max(most, Math.Abs(painted[i + channel] - shaded[i + channel]));
            }

            Assert.True(
                most <= 1,
                $"pixel ({i / 4 % picture.Width}, {i / 4 / picture.Width}) is {Colour(shaded, i)} shaded and {Colour(painted, i)} painted");
            Assert.Equal(255, shaded[i + 3]);

            differing += most > 0 ? 1 : 0;
        }

        Assert.True(
            differing * 1000 < painted.Length / 4,
            $"{differing} of {painted.Length / 4} pixels differ by a level");
    }

    private void AssertShadedExactlyAsPainted(ExploreSurface picture)
    {
        var quads = picture.Quads()!;
        var painted = new byte[PixelBuffer.LengthFor(picture.Width, picture.Height)];

        picture.Painter(Ground).Paint(painted, new CanvasRegion(0, 0, picture.Width, picture.Height));

        var shaded = Shaded(quads);

        Assert.True(quads.Quads.Length > 100, $"only {quads.Quads.Length} shapes drawn, too few to say anything");

        for (var i = 0; i < painted.Length; i += 4)
        {
            Assert.True(
                painted.AsSpan(i, 4).SequenceEqual(shaded.AsSpan(i, 4)),
                $"pixel ({i / 4 % picture.Width}, {i / 4 / picture.Width}) is {Colour(shaded, i)} shaded and {Colour(painted, i)} painted");
        }
    }

    private byte[] Shaded(CushionQuads quads) =>
        _renderer.Shade(quads, Ground, Whole(quads), light: null).GetPixelBytes(0, 0, quads.Width, quads.Height);

    private static CanvasRegion Whole(CushionQuads quads) => new(0, 0, quads.Width, quads.Height);

    private static string Colour(byte[] pixels, int i) => $"#{pixels[i + 2]:X2}{pixels[i + 1]:X2}{pixels[i]:X2}";

    private static ExploreSurface Picture(MapViewport viewport, int width = Width, int height = Height, ShapeColours? colours = null, MapStyle? style = null) =>
        ExploreSurface.Create(
            Tree,
            Tree.RootNode,
            ExploreView.Treemap,
            width,
            height,
            1,
            1,
            colours ?? ShapeColours.ByBranch(ExploreScheme.Standard),
            ExploreSpacing.Comfortable,
            VolumeSpace.None,
            viewport,
            style);

    /// <summary>
    /// Folders inside folders four levels down, each with files of falling sizes, so the picture has
    /// bands, cushions at every depth, and slivers too thin to cover a pixel.
    /// </summary>
    private static readonly ExploreTree Tree = Build();

    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder("Sample");

        Fill(builder, ExploreTreeBuilder.RootNode, depth: 0);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static void Fill(ExploreTreeBuilder builder, int folder, int depth)
    {
        var folders = depth < 3 ? 4 : 0;
        var children = new List<ExploreChild>();

        for (var i = 0; i < folders; i++)
        {
            children.Add(new ExploreChild($"folder{i}", IsDirectory: true, IsLink: false, Size: 0));
        }

        for (var i = 0; i < 12; i++)
        {
            children.Add(new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: (12 - i) * (12 - i) * 1000L));
        }

        var first = builder.AddChildren(folder, [.. children]);

        for (var i = 0; i < folders; i++)
        {
            Fill(builder, first + i, depth + 1);
        }
    }
}
