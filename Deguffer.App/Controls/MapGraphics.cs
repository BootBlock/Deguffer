using System.Runtime.CompilerServices;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
/// hand-over for regions a fraction of that size, and it never goes up whole: a map's drawing goes up
/// a region at a time into a GPU canvas of its own (see <see cref="PaintUpload"/>), which the
/// overview is shrunk from once all of it is there.</para>
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

    /// <summary>The shaders every renderer here is made with.</summary>
    private readonly CushionShaders _shaders;

    /// <summary>The GPU bitmap a region is staged in on its way to a surface, made once per device.</summary>
    private CanvasBitmap? _staging;

    /// <summary>
    /// What shades a canvas on the current device, or null where the device cannot run its shaders.
    /// Made once per device, the first time it is asked for, so a device that cannot have one is asked
    /// once. A loss as it is made replaces the device, and the new one is asked again.
    /// </summary>
    private Lazy<CushionRenderer?> _renderer;

    /// <summary>
    /// The graphics for <paramref name="device"/>, whose renderers are made with
    /// <paramref name="shaders"/>.
    ///
    /// <para>The shaders start compiling here, on a worker. A map makes its graphics as it is
    /// constructed, so the compilation overlaps whatever comes before its first picture, such as the
    /// rest of its page being built and its tree being scanned or read, rather than starting on the UI
    /// thread when that picture asks whether it can be shaded.</para>
    /// </summary>
    internal MapGraphics(CompositionDevice device, CushionShaders shaders)
    {
        Device = device;
        Geometry = new MapGeometry(() => device.Current);
        _shaders = shaders;
        _renderer = RendererOnDevice();

        _ = shaders.Warm();

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

    /// <summary>
    /// How many bytes of painted pixels have gone up to the GPU, every one of them through
    /// <see cref="Send"/>. Read by a test, which proves a drawing painted on the CPU goes up once, and
    /// that its overview sends nothing more.
    /// </summary>
    internal long Uploaded { get; private set; }

    /// <summary>The graphics for <paramref name="compositor"/>, made the first time a map asks.</summary>
    public static MapGraphics For(Compositor compositor) =>
        Shared.GetValue(compositor, static compositor => new MapGraphics(CompositionDevice.For(compositor), CushionShaders.Default));

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
    /// A plain surface, one texture, <paramref name="width"/> by <paramref name="height"/>:
    /// <paramref name="spare"/>, resized, where there is one, rather than a surface made again (G5). The
    /// first write into it has to cover all of it.
    ///
    /// <para>For a tile of a moving map (see <see cref="MapTiling"/>), which is written whole each time
    /// it is used and never in part, and for a page of the map's names (see <see cref="NameSheet"/>),
    /// which many brushes show at once and which a virtual surface is far slower to draw through.</para>
    ///
    /// <para>A tile is given its own size, never <see cref="MapTiling.Size"/> square: a tile in the last
    /// column or row of the picture is smaller, and written into a whole square, it was refused as an
    /// invalid argument.</para>
    /// </summary>
    public CompositionDrawingSurface PlainSurface(CompositionDrawingSurface? spare, int width, int height) =>
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
            var staging = _staging ??= Blank(device, PaintOrder.RegionSize, PaintOrder.RegionSize);

            Send(staging, 0, 0, region);

            CopyInto(surface, staging, new Point(0, 0), region, flush: false);
        });
    }

    /// <summary>
    /// <paramref name="spare"/> where it is a canvas <paramref name="width"/> by
    /// <paramref name="height"/> on the device drawn through now, or else a new one with nothing
    /// written into it yet, for <see cref="Write(CompositionDrawingSurface, CanvasBitmap, byte[], int, CanvasRegion)"/>.
    /// A spare that is not returned is disposed. Null where the device was lost on the way.
    /// </summary>
    public CanvasBitmap? Canvas(CanvasBitmap? spare, int width, int height) =>
        Device.UnlessLost<CanvasBitmap>(device =>
        {
            if (spare is not null
                && IsOnDevice(spare, device)
                && spare.SizeInPixels.Width == width
                && spare.SizeInPixels.Height == height)
            {
                return spare;
            }

            spare?.Dispose();

            return Blank(device, width, height);
        });

    /// <summary>
    /// Write <paramref name="region"/> of <paramref name="pixels"/>, a canvas <paramref name="width"/>
    /// pixels across, into the same place on <paramref name="canvas"/>, a GPU bitmap of that canvas's
    /// size, and from there into the same place on <paramref name="surface"/>, and nothing else of
    /// either. Once every region is written, all of the canvas is on the GPU for <see cref="Shrink"/>,
    /// and none of it went up twice. Nothing is written with a canvas made on a device a loss has
    /// replaced: see <see cref="IsOnDevice"/>.
    /// </summary>
    public void Write(CompositionDrawingSurface surface, CanvasBitmap canvas, byte[] pixels, int width, CanvasRegion region)
    {
        PixelBuffer.CopyRegion(pixels, width, region, _staged);

        Device.UnlessLost(device =>
        {
            if (!IsOnDevice(canvas, device))
            {
                return;
            }

            Send(canvas, region.X, region.Y, region);

            CopyInto(surface, canvas, new Point(region.X, region.Y), region, flush: false);
        });
    }

    /// <summary>
    /// Shade <paramref name="quads"/> on <paramref name="ground"/> into the corner of
    /// <paramref name="surface"/> their canvas covers, every pixel of it. Asked only while
    /// <see cref="Shades"/> says so.
    /// </summary>
    /// <returns>
    /// The GPU canvas the quads were shaded on, for <see cref="Shrink"/> straight after, before anything
    /// else is shaded over it; or null where nothing was shaded, on a device lost or unable to run the
    /// shaders.
    /// </returns>
    public CanvasBitmap? Shade(CompositionDrawingSurface surface, CushionQuads quads, TileColour ground) =>
        Shade(surface, quads, ground, new CanvasRegion(0, 0, quads.Width, quads.Height), light: null);

    /// <summary>
    /// Shade <paramref name="region"/> of the canvas <paramref name="quads"/> make on
    /// <paramref name="ground"/> into the same place on <paramref name="surface"/>, under
    /// <paramref name="light"/> where there is one, and nothing else of it: the pointer's light moving
    /// over a canvas shaded whole before. Returns the canvas shaded on, as the overload for the whole
    /// canvas does.
    /// </summary>
    public CanvasBitmap? Shade(
        CompositionDrawingSurface surface,
        CushionQuads quads,
        TileColour ground,
        CanvasRegion region,
        PointerLight? light) =>
        Device.UnlessLost<CanvasBitmap>(_ =>
        {
            // None where the device was lost as the renderer was made: it is replaced, and every
            // drawing is shaded again on the new one.
            if (_renderer.Value is not { } renderer)
            {
                return null;
            }

            var shaded = renderer.Shade(quads, ground, region, light);

            // Flushed now rather than when Direct2D chooses. The renderer writes its canvas straight
            // through Direct3D, so Direct2D cannot see that the next shading overwrites what this copy
            // reads, and ending the session leaves the copy queued. Measured: a new drawing landed with
            // the light shaded into the drawing before it, a few milliseconds later, and nothing else.
            CopyInto(surface, shaded, new Point(region.X, region.Y), region, flush: true);

            return shaded;
        });

    /// <summary>
    /// The corner of <paramref name="canvas"/> a canvas <paramref name="width"/> by
    /// <paramref name="height"/> covers, drawn small enough to fill <paramref name="into"/> at
    /// <paramref name="size"/> pixels across: the overview of a whole picture. A surface is made where
    /// there is none to draw into, and the one drawn into is returned, or null where the device was
    /// lost on the way.
    ///
    /// <para>From the canvas already on the GPU, the one it was shaded on or the one it went up into a
    /// region at a time (see <see cref="PaintUpload"/>), so nothing more goes up through the UI thread.
    /// The GPU shrinks it, with a filter that takes every pixel into account, so a treemap's fine lines
    /// thin out rather than break up as they do when a large surface is shown small.</para>
    ///
    /// <para>Null, too, for a canvas made on a device a loss has replaced: see <see cref="IsOnDevice"/>.</para>
    /// </summary>
    public CompositionDrawingSurface? Shrink(CompositionDrawingSurface? into, CanvasBitmap canvas, int width, int height, SizeInt32 size) =>
        Device.UnlessLost<CompositionDrawingSurface>(device =>
        {
            if (!IsOnDevice(canvas, device))
            {
                return null;
            }

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

            // Before the next shading, or the next drawing's regions, overwrites the canvas this reads,
            // as Shade does.
            session.Flush();

            return surface;
        });

    /// <summary>
    /// <paramref name="shape"/>, in a canvas's own pixels, filled opaque on a clear ground into
    /// <paramref name="area"/> of that canvas, drawn from the area's corner: the mask a lifted shape is
    /// cut out by (see <see cref="ExploreLift"/>). <paramref name="into"/> is resized rather than made
    /// again where there is one (G5), and the surface drawn into is returned, or null where the device
    /// was lost on the way.
    /// </summary>
    public CompositionDrawingSurface? Fill(CompositionDrawingSurface? into, CanvasGeometry shape, CanvasRegion area) =>
        Device.UnlessLost(_ =>
        {
            var surface = Sized(into, new SizeInt32(area.Width, area.Height));

            using var session = CanvasComposition.CreateDrawingSession(surface);

            session.Clear(Microsoft.UI.Colors.Transparent);
            session.FillGeometry(shape, -area.X, -area.Y, Microsoft.UI.Colors.White);

            return surface;
        });

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

    /// <summary>
    /// A bitmap <paramref name="width"/> by <paramref name="height"/> on <paramref name="device"/>, with
    /// nothing written into it yet. A render target rather than a bitmap made from bytes, so making it
    /// sends nothing up: every pixel of it that is read goes up through <see cref="Send"/> first.
    /// </summary>
    private static CanvasRenderTarget Blank(CanvasDevice device, int width, int height) =>
        new(device, width, height, 96, CompositionDevice.BitmapFormat, CanvasAlphaMode.Premultiplied);

    /// <summary>
    /// Send <paramref name="region"/>'s pixels, packed in the staged array, up into
    /// <paramref name="bitmap"/> at <paramref name="x"/>, <paramref name="y"/>. The one place painted
    /// pixels go up to the GPU, so <see cref="Uploaded"/> counts every byte.
    /// </summary>
    private void Send(CanvasBitmap bitmap, int x, int y, CanvasRegion region)
    {
        bitmap.SetPixelBytes(_staged, x, y, region.Width, region.Height);
        Uploaded += PixelBuffer.LengthFor(region.Width, region.Height);
    }

    /// <summary>
    /// Whether <paramref name="bitmap"/> was made on <paramref name="device"/>, the device drawn through
    /// now. A map's GPU canvas (see <see cref="PaintUpload"/>) outlives a loss until the map hears the
    /// device was replaced, and a hand-over can go on landing regions after a loss met earlier in it.
    /// Drawn through the new device, a bitmap of the old one fails with an error that is not a loss,
    /// which <see cref="CompositionDevice.UnlessLost{T}"/> would let through to the UI thread.
    /// </summary>
    private static bool IsOnDevice(CanvasBitmap bitmap, CanvasDevice device) => ReferenceEquals(bitmap.Device, device);

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
        new(() => Device.UnlessLost(device => CushionRenderer.For(device, _shaders)), LazyThreadSafetyMode.None);

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
