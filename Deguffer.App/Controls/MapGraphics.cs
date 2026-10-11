using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics;

namespace Deguffer.App.Controls;

/// <summary>
/// The GPU device every map draws through, and the one way a painted region of a canvas reaches the
/// composition surface it is shown in. The window takes its stills through it too: see
/// <see cref="Capture"/>.
///
/// <para>One for the application, because the device is the costly thing and the window has the one
/// compositor (G5). Each map's surfaces are made here, on the one device, so a device lost is replaced
/// for all of them at once, and each map then makes its surfaces again: see <see cref="Replaced"/>.</para>
///
/// <para>A treemap's canvas is shaded here on the GPU when the compositor says effects are fast (see
/// <see cref="Shades"/>), in one pass from its quads. Otherwise it is painted on the CPU, by
/// <see cref="CanvasPainter"/>, and only what each region holds is written to the GPU, a region at a
/// time as it lands. A whole canvas is 33 MB at 4K, too much to send through the UI thread at every
/// hand-over for regions a fraction of that size.</para>
/// </summary>
internal sealed class MapGraphics
{
    private static MapGraphics? _shared;

    private readonly CompositionGraphicsDevice _graphics;

    /// <summary>Whether the compositor has the GPU time for effects, which a remote session can change.</summary>
    private readonly CompositionCapabilities _capabilities = new();

    /// <summary>
    /// One region's pixels, packed, before they go up. A region is never larger than
    /// <see cref="PaintOrder.RegionSize"/> on a side, and an upload reads only as many bytes as its
    /// region holds, so the one array serves every region (G5).
    /// </summary>
    private readonly byte[] _staged = new byte[PaintOrder.RegionSize * PaintOrder.RegionSize * 4];

    private CanvasDevice _device;

    /// <summary>The GPU bitmap a region is staged in on its way to a surface, made once per device.</summary>
    private CanvasBitmap? _staging;

    /// <summary>What shades a canvas on the device, made the first time it is asked for: see <see cref="Renderer"/>.</summary>
    private CushionRenderer? _renderer;

    /// <summary>Whether <see cref="_renderer"/> has been asked for on this device, so a device that cannot have one is asked once.</summary>
    private bool _rendererAsked;

    private MapGraphics(Compositor compositor)
    {
        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);

        Geometry = new MapGeometry(() => _device);
        _graphics.RenderingDeviceReplaced += (_, _) =>
        {
            Generation++;
            Replaced?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Every surface's content is gone: the device was lost and replaced, by this or by the compositor.
    /// Raised on the UI thread. Each map draws its picture again, and keeps none of what it had.
    ///
    /// <para>Nor does it keep its surfaces. One made before the replacement takes a write on the new
    /// device without an error and shows nothing of it, so the map stayed empty under its labels
    /// until each surface was made again.</para>
    /// </summary>
    public event EventHandler? Replaced;

    /// <summary>The outlines and the shapes over the picture, built on this device.</summary>
    public MapGeometry Geometry { get; }

    /// <summary>
    /// How many times the device has been replaced, so a map that was off the screen when it happened
    /// knows on its return that what it kept is gone.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Whether a treemap's canvas is shaded on the GPU (see <see cref="Shade"/>) rather than painted on
    /// the CPU. Asked at each redraw, so a change of the compositor's word is followed at the next.
    ///
    /// <para>Only where the compositor says effects are fast, which a remote session is not, and only on
    /// a hardware device. A software device's GPU time is the CPU's: WARP shades a 4K canvas in about
    /// 20 ms on the UI thread, where the CPU paints it in about as long across all of its threads, a region
    /// at a time and off the UI thread. Win2D's device is asked rather than the compositor's word alone,
    /// because the two are separate devices. A device that cannot run the shaders is painted for too.</para>
    /// </summary>
    public bool Shades => _capabilities.AreEffectsFast() && Renderer is { OnSoftware: false };

    /// <summary>The device for <paramref name="compositor"/>, made the first time a map asks.</summary>
    public static MapGraphics For(Compositor compositor) => _shared ??= new MapGraphics(compositor);

    /// <summary>
    /// A surface for a canvas of this size, transparent until a region is written into it.
    ///
    /// <para>A virtual surface, because only what is written into one takes memory, and it can be
    /// emptied without being made again (see <see cref="Empty"/>). A plain surface has to be drawn
    /// whole first and is one texture, which a large canvas runs into the limit of.</para>
    /// </summary>
    public CompositionVirtualDrawingSurface CreateSurface(int width, int height) =>
        _graphics.CreateVirtualDrawingSurface(
            new SizeInt32(width, height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

    /// <summary>
    /// A surface for one tile of a moving map (see <see cref="MapTiling"/>), written whole each time it
    /// is used. A plain surface rather than a virtual one, because a tile is never written in part.
    /// </summary>
    public CompositionDrawingSurface CreateTileSurface() =>
        _graphics.CreateDrawingSurface2(
            new SizeInt32(MapTiling.Size, MapTiling.Size),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

    /// <summary>
    /// Let go of everything written into <paramref name="surface"/>, which shows nothing until it is
    /// written again. Trimming keeps what lies inside the rectangles it is given, so none are given.
    /// </summary>
    public static void Empty(CompositionVirtualDrawingSurface surface) => surface.Trim([]);

    /// <summary>
    /// Give back what the device keeps for itself between drawings, for a window nobody can see. What
    /// the surfaces hold is theirs and stays.
    /// </summary>
    public void Trim() => _device.Trim();

    /// <summary>
    /// Write <paramref name="region"/> of <paramref name="pixels"/>, a canvas <paramref name="width"/>
    /// pixels across, into the same place on <paramref name="surface"/>, and nothing else of it.
    /// </summary>
    public void Write(CompositionDrawingSurface surface, byte[] pixels, int width, CanvasRegion region)
    {
        PixelBuffer.CopyRegion(pixels, width, region, _staged);

        // A pass can meet the same loss at several regions, and skips each: everything is drawn again on
        // the new device.
        UnlessLost(device =>
        {
            var staging = _staging ??= CanvasBitmap.CreateFromBytes(
                device,
                _staged,
                PaintOrder.RegionSize,
                PaintOrder.RegionSize,

                // Win2D's own bitmaps take the platform's format type, where the compositor's
                // surfaces above take the Windows App SDK's: the same format, named twice.
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

            staging.SetPixelBytes(_staged, 0, 0, region.Width, region.Height);

            // The session draws from the update rectangle's own corner, wherever the compositor keeps
            // that part of the surface, and every pixel of it is overwritten, as a draw into a surface
            // has to: what was there before is undefined.
            using var session = CanvasComposition.CreateDrawingSession(
                surface,
                new Rect(region.X, region.Y, region.Width, region.Height),
                96);

            session.DrawImage(
                staging,
                0,
                0,
                new Rect(0, 0, region.Width, region.Height),
                1,
                CanvasImageInterpolation.NearestNeighbor,
                CanvasComposite.Copy);
        });
    }

    /// <summary>
    /// Shade <paramref name="quads"/> on <paramref name="ground"/> into the corner of
    /// <paramref name="surface"/> their canvas covers, every pixel of it, and keep the canvas for
    /// <see cref="ShrinkShaded"/>. Asked only while <see cref="Shades"/> says so.
    /// </summary>
    public void Shade(CompositionDrawingSurface surface, CushionQuads quads, TileColour ground) =>
        UnlessLost(_ =>
        {
            // None where the device was lost as the renderer was made: it is replaced, and every
            // drawing is shaded again on the new one.
            if (Renderer is not { } renderer)
            {
                return;
            }

            var shaded = renderer.Shade(quads, ground);

            // Every pixel of the update rectangle is overwritten, as a draw into a surface has to be.
            using var session = CanvasComposition.CreateDrawingSession(
                surface,
                new Rect(0, 0, quads.Width, quads.Height),
                96);

            session.DrawImage(
                shaded,
                0,
                0,
                new Rect(0, 0, quads.Width, quads.Height),
                1,
                CanvasImageInterpolation.NearestNeighbor,
                CanvasComposite.Copy);
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
        UnlessLost(device =>
        {
            using var bitmap = CanvasBitmap.CreateFromBytes(
                device,
                pixels,
                width,
                height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

            return Shrink(into, bitmap, width, height, size);
        });

    /// <summary>
    /// The canvas <see cref="Shade"/> shaded last, <paramref name="width"/> by <paramref name="height"/>,
    /// drawn small as <see cref="Shrink(CompositionDrawingSurface?, byte[], int, int, SizeInt32)"/> draws
    /// a painted one. Asked straight after the shading, before anything else is shaded.
    /// </summary>
    public CompositionDrawingSurface? ShrinkShaded(CompositionDrawingSurface? into, int width, int height, SizeInt32 size) =>
        _renderer is { Shaded: { } shaded } ? UnlessLost(_ => Shrink(into, shaded, width, height, size)) : null;

    /// <summary>Draw the corner of <paramref name="canvas"/> a canvas covers into <paramref name="into"/>, small.</summary>
    private CompositionDrawingSurface Shrink(
        CompositionDrawingSurface? into,
        CanvasBitmap canvas,
        int width,
        int height,
        SizeInt32 size)
    {
        // Resized rather than made again for each whole picture (G5).
        var surface = into ?? _graphics.CreateDrawingSurface2(
            size,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        if (surface.SizeInt32 != size)
        {
            surface.Resize(size);
        }

        using var session = CanvasComposition.CreateDrawingSession(surface);

        session.Clear(Microsoft.UI.Colors.Transparent);
        session.DrawImage(
            canvas,
            new Rect(0, 0, size.Width, size.Height),
            new Rect(0, 0, width, height),
            1,
            CanvasImageInterpolation.HighQualityCubic);

        return surface;
    }

    /// <summary>
    /// What <paramref name="draw"/> returns on the device, or null where the device was lost on the way,
    /// which is then replaced.
    ///
    /// <para>The one failure to give up a drawing for: everything is drawn again on the new device. Any
    /// other failure is a defect, an invalid argument above all, and is left to be raised. Several
    /// drawings can meet the same loss, and the device it was met on is reported lost only once.</para>
    /// </summary>
    private T? UnlessLost<T>(Func<CanvasDevice, T?> draw)
        where T : class
    {
        var device = _device;

        try
        {
            return draw(device);
        }
        catch (Exception exception) when (device.IsDeviceLost(exception.HResult))
        {
            if (device == _device)
            {
                device.RaiseDeviceLost();
            }

            return null;
        }
    }

    /// <summary>Do <paramref name="draw"/> on the device, unless the device is lost on the way: see <see cref="UnlessLost{T}"/>.</summary>
    private void UnlessLost(Action<CanvasDevice> draw) =>
        UnlessLost(device =>
        {
            draw(device);
            return this;
        });

    /// <summary>
    /// A still of <paramref name="visual"/> and everything drawn under it as it is now, at
    /// <paramref name="size"/> pixels, or null where the device was lost on the way. On this device
    /// rather than a second one, because the window is the one asking and it has the one compositor
    /// (G5).
    /// </summary>
    public async Task<ICompositionSurface?> Capture(Visual visual, SizeInt32 size)
    {
        var device = _device;

        try
        {
            return await _graphics.CaptureAsync(
                visual,
                size,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied,
                1);
        }
        catch (Exception exception) when (device.IsDeviceLost(exception.HResult))
        {
            // As a lost device is everywhere else here: replaced, and the still not taken. Any other
            // failure is a defect, and is left to be raised.
            if (device == _device)
            {
                device.RaiseDeviceLost();
            }

            return null;
        }
    }

    /// <summary>
    /// What shades a canvas on the current device, or null where the device cannot run its shaders.
    /// Made once per device, the first time it is asked for, and never on a device lost since.
    /// </summary>
    private CushionRenderer? Renderer
    {
        get
        {
            if (!_rendererAsked)
            {
                // Asked once per device. A loss as it is made replaces the device, and the new one is
                // asked again.
                _rendererAsked = true;
                _renderer = UnlessLost(CushionRenderer.For);
            }

            return _renderer;
        }
    }

    /// <summary>
    /// The device was lost: replace it under the compositor's graphics device, which raises
    /// <see cref="Replaced"/> once every surface is on the new one.
    /// </summary>
    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        sender.DeviceLost -= OnDeviceLost;

        _staging = null;
        _renderer?.Dispose();
        _renderer = null;
        _rendererAsked = false;
        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        CanvasComposition.SetCanvasDevice(_graphics, _device);
    }
}
