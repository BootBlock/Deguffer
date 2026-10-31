using System.Runtime.CompilerServices;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics;

namespace Deguffer.App.Controls;

/// <summary>
/// The one way a painted region of a canvas reaches the composition surface it is shown in, drawn
/// through the compositor's device (see <see cref="CompositionDevice"/>).
///
/// <para>One for each compositor, as the device is (G5). What it keeps, the bitmap a region is staged
/// in and what shades a canvas, is made on that device, and made again once a loss replaces it.</para>
///
/// <para>A treemap's canvas is shaded here on the GPU when the compositor says effects are fast (see
/// <see cref="Shades"/>), in one pass from its quads. Otherwise it is painted on the CPU, by
/// <see cref="CanvasPainter"/>, and only what each region holds is written to the GPU, a region at a
/// time as it lands. A whole canvas is 33 MB at 4K, too much to send through the UI thread at every
/// hand-over for regions a fraction of that size.</para>
/// </summary>
internal sealed class MapGraphics
{
    private static readonly ConditionalWeakTable<Compositor, MapGraphics> Shared = [];

    /// <summary>
    /// One region's pixels, packed, before they go up. A region is never larger than
    /// <see cref="PaintOrder.RegionSize"/> on a side, and an upload reads only as many bytes as its
    /// region holds, so the one array serves every region (G5).
    /// </summary>
    private readonly byte[] _staged = new byte[PaintOrder.RegionSize * PaintOrder.RegionSize * 4];

    /// <summary>The GPU bitmap a region is staged in on its way to a surface, made once per device.</summary>
    private CanvasBitmap? _staging;

    /// <summary>
    /// What shades a canvas on the current device, or null where the device cannot run its shaders.
    /// Made once per device, the first time it is asked for, so a device that cannot have one is asked
    /// once. A loss as it is made replaces the device, and the new one is asked again.
    /// </summary>
    private Lazy<CushionRenderer?> _renderer;

    private MapGraphics(CompositionDevice device)
    {
        Device = device;
        Geometry = new MapGeometry(() => device.Current);
        _renderer = RendererOnDevice();

        device.Lost += OnDeviceLost;
    }

    /// <summary>The device every surface here is made on and drawn through.</summary>
    public CompositionDevice Device { get; }

    /// <summary>The outlines and the shapes over the picture, built on this device.</summary>
    public MapGeometry Geometry { get; }

    /// <summary>
    /// Whether a treemap's canvas is shaded on the GPU (see <see cref="Shade(CompositionDrawingSurface, CushionQuads, TileColour)"/>) rather than painted on
    /// the CPU. Asked at each redraw, so a change of the compositor's word is followed at the next. The
    /// word is kept by <see cref="SystemLook"/>, because a moving map asks this at every camera report.
    ///
    /// <para>Only where the compositor says effects are fast, which a remote session is not, and only on
    /// a hardware device. A software device's GPU time is the CPU's: WARP shades a 4K canvas in about
    /// 20 ms on the UI thread, where the CPU paints it in about as long across all of its threads, a region
    /// at a time and off the UI thread. Win2D's device is asked rather than the compositor's word alone,
    /// because the two are separate devices. A device that cannot run the shaders is painted for too.</para>
    /// </summary>
    public bool Shades => SystemLook.Current.State.EffectsFast && _renderer.Value is { OnSoftware: false };

    /// <summary>
    /// The bitmap regions are staged in now, or null before the first write on this device. Read by a
    /// test, which proves a loss lets go of it rather than leaving it to the finaliser.
    /// </summary>
    internal CanvasBitmap? Staging => _staging;

    /// <summary>The graphics for <paramref name="compositor"/>, made the first time a map asks.</summary>
    public static MapGraphics For(Compositor compositor) =>
        Shared.GetValue(compositor, static compositor => new MapGraphics(CompositionDevice.For(compositor)));

    /// <summary>
    /// A surface for a canvas of this size, transparent until a region is written into it.
    ///
    /// <para>A virtual surface, because only what is written into one takes memory, and it can be
    /// emptied without being made again (see <see cref="Empty"/>). A plain surface has to be drawn
    /// whole first and is one texture, which a large canvas runs into the limit of.</para>
    /// </summary>
    public CompositionVirtualDrawingSurface CreateSurface(int width, int height) =>
        Device.Graphics.CreateVirtualDrawingSurface(
            new SizeInt32(width, height),
            CompositionDevice.Format,
            CompositionDevice.Alpha);

    /// <summary>
    /// A surface for one tile of a moving map (see <see cref="MapTiling"/>), <paramref name="width"/> by
    /// <paramref name="height"/> and written whole each time it is used: <paramref name="spare"/>, resized
    /// to the tile, where there is one, rather than a surface made again (G5). A plain surface rather than
    /// a virtual one, because a tile is never written in part.
    ///
    /// <para>The tile's own size, never <see cref="MapTiling.Size"/> square. The first write into a plain
    /// surface has to cover all of it, and a tile in the last column or row of the picture is smaller:
    /// written into a whole square, it was refused as an invalid argument.</para>
    /// </summary>
    public CompositionDrawingSurface TileSurface(CompositionDrawingSurface? spare, int width, int height) =>
        Sized(spare, new SizeInt32(width, height));

    /// <summary>
    /// Let go of everything written into <paramref name="surface"/>, which shows nothing until it is
    /// written again. Trimming keeps what lies inside the rectangles it is given, so none are given.
    /// </summary>
    public static void Empty(CompositionVirtualDrawingSurface surface) => surface.Trim([]);

    /// <summary>
    /// Write <paramref name="region"/> of <paramref name="pixels"/>, a canvas <paramref name="width"/>
    /// pixels across, into the same place on <paramref name="surface"/>, and nothing else of it.
    /// </summary>
    public void Write(CompositionDrawingSurface surface, byte[] pixels, int width, CanvasRegion region)
    {
        PixelBuffer.CopyRegion(pixels, width, region, _staged);

        Device.UnlessLost(device =>
        {
            var staging = _staging ??= CanvasBitmap.CreateFromBytes(
                device,
                _staged,
                PaintOrder.RegionSize,
                PaintOrder.RegionSize,
                CompositionDevice.BitmapFormat);

            staging.SetPixelBytes(_staged, 0, 0, region.Width, region.Height);

            CopyInto(surface, staging, new Point(0, 0), region, flush: false);
        });
    }

    /// <summary>
    /// Shade <paramref name="quads"/> on <paramref name="ground"/> into the corner of
    /// <paramref name="surface"/> their canvas covers, every pixel of it, and keep the canvas for
    /// <see cref="ShrinkShaded"/>. Asked only while <see cref="Shades"/> says so.
    /// </summary>
    public void Shade(CompositionDrawingSurface surface, CushionQuads quads, TileColour ground) =>
        Shade(surface, quads, ground, new CanvasRegion(0, 0, quads.Width, quads.Height), light: null);

    /// <summary>
    /// Shade <paramref name="region"/> of the canvas <paramref name="quads"/> make on
    /// <paramref name="ground"/> into the same place on <paramref name="surface"/>, under
    /// <paramref name="light"/> where there is one, and nothing else of it: the pointer's light moving
    /// over a canvas shaded whole before.
    /// </summary>
    public void Shade(
        CompositionDrawingSurface surface,
        CushionQuads quads,
        TileColour ground,
        CanvasRegion region,
        PointerLight? light) =>
        Device.UnlessLost(_ =>
        {
            // None where the device was lost as the renderer was made: it is replaced, and every
            // drawing is shaded again on the new one.
            if (_renderer.Value is not { } renderer)
            {
                return;
            }

            var shaded = renderer.Shade(quads, ground, region, light);

            // Flushed now rather than when Direct2D chooses. The renderer writes its canvas straight
            // through Direct3D, so Direct2D cannot see that the next shading overwrites what this copy
            // reads, and ending the session leaves the copy queued. Measured: a new drawing landed with
            // the light shaded into the drawing before it, a few milliseconds later, and nothing else.
            CopyInto(surface, shaded, new Point(region.X, region.Y), region, flush: true);
        });

    /// <summary>
    /// <paramref name="pixels"/>, a canvas <paramref name="width"/> by <paramref name="height"/>,
    /// drawn small enough to fill <paramref name="into"/> at <paramref name="size"/> pixels across: the
    /// overview of a whole picture. A surface is made where there is none to draw into, and the one
    /// drawn into is returned, or null where the device was lost on the way.
    ///
    /// <para>Once per whole picture, which is the one time the whole canvas goes up through the UI
    /// thread, and only once it has been painted. The GPU shrinks it, with a filter that takes every
    /// pixel into account, so a treemap's fine lines thin out rather than break up as they do when a
    /// large surface is shown small.</para>
    /// </summary>
    public CompositionDrawingSurface? Shrink(CompositionDrawingSurface? into, byte[] pixels, int width, int height, SizeInt32 size) =>
        Device.UnlessLost(device =>
        {
            using var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, width, height, CompositionDevice.BitmapFormat);

            return Shrink(into, bitmap, width, height, size);
        });

    /// <summary>
    /// The canvas <see cref="Shade(CompositionDrawingSurface, CushionQuads, TileColour)"/> shaded last, <paramref name="width"/> by <paramref name="height"/>,
    /// drawn small as <see cref="Shrink(CompositionDrawingSurface?, byte[], int, int, SizeInt32)"/> draws
    /// a painted one. Asked straight after the shading, before anything else is shaded.
    /// </summary>
    public CompositionDrawingSurface? ShrinkShaded(CompositionDrawingSurface? into, int width, int height, SizeInt32 size) =>
        _renderer is { IsValueCreated: true, Value.Shaded: { } shaded }
            ? Device.UnlessLost(_ => Shrink(into, shaded, width, height, size))
            : null;

    /// <summary>
    /// Copy <paramref name="image"/> from <paramref name="from"/> into <paramref name="region"/> of
    /// <paramref name="surface"/>, every pixel of it, as a draw into a surface has to: what was there
    /// before is undefined. The session draws from the update rectangle's own corner, wherever the
    /// compositor keeps that part of the surface.
    ///
    /// <para><paramref name="flush"/> sends the copy to the GPU before the session ends, for an image
    /// Direct3D writes where Direct2D cannot see it.</para>
    /// </summary>
    private static void CopyInto(CompositionDrawingSurface surface, CanvasBitmap image, Point from, CanvasRegion region, bool flush)
    {
        using var session = CanvasComposition.CreateDrawingSession(
            surface,
            new Rect(region.X, region.Y, region.Width, region.Height),
            96);

        session.DrawImage(
            image,
            0,
            0,
            new Rect(from.X, from.Y, region.Width, region.Height),
            1,
            CanvasImageInterpolation.NearestNeighbor,
            CanvasComposite.Copy);

        if (flush)
        {
            session.Flush();
        }
    }

    /// <summary>Draw the corner of <paramref name="canvas"/> a canvas covers into <paramref name="into"/>, small.</summary>
    private CompositionDrawingSurface Shrink(
        CompositionDrawingSurface? into,
        CanvasBitmap canvas,
        int width,
        int height,
        SizeInt32 size)
    {
        // Resized rather than made again for each whole picture (G5).
        var surface = Sized(into, size);

        using var session = CanvasComposition.CreateDrawingSession(surface);

        session.Clear(Microsoft.UI.Colors.Transparent);
        session.DrawImage(
            canvas,
            new Rect(0, 0, size.Width, size.Height),
            new Rect(0, 0, width, height),
            1,
            CanvasImageInterpolation.HighQualityCubic);

        // Before the next shading overwrites the renderer's canvas this may be reading, as Shade does.
        session.Flush();

        return surface;
    }

    /// <summary><paramref name="spare"/> resized to <paramref name="size"/>, or a plain surface of that size where there is none.</summary>
    private CompositionDrawingSurface Sized(CompositionDrawingSurface? spare, SizeInt32 size)
    {
        if (spare is null)
        {
            return Device.Graphics.CreateDrawingSurface2(size, CompositionDevice.Format, CompositionDevice.Alpha);
        }

        if (spare.SizeInt32 != size)
        {
            spare.Resize(size);
        }

        return spare;
    }

    /// <summary>The renderer for the device drawn through now, made when it is first asked for.</summary>
    private Lazy<CushionRenderer?> RendererOnDevice() =>
        new(() => Device.UnlessLost(CushionRenderer.For), LazyThreadSafetyMode.None);

    /// <summary>The device was lost: let go of everything made on it, to be made again on the next.</summary>
    private void OnDeviceLost(object? sender, EventArgs args)
    {
        _staging?.Dispose();
        _staging = null;

        if (_renderer.IsValueCreated)
        {
            _renderer.Value?.Dispose();
        }

        _renderer = RendererOnDevice();
    }
}
