using System.Numerics;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
/// <para>The canvas is still painted on the CPU, by <see cref="CanvasPainter"/>, and only what each
/// region holds is written to the GPU, a region at a time as it lands. A whole canvas is 33 MB at 4K,
/// too much to send through the UI thread at every hand-over for regions a fraction of that size.</para>
/// </summary>
internal sealed class MapGraphics
{
    private static MapGraphics? _shared;

    private readonly CompositionGraphicsDevice _graphics;

    /// <summary>
    /// One region's pixels, packed, before they go up. A region is never larger than
    /// <see cref="PaintOrder.RegionSize"/> on a side, and an upload reads only as many bytes as its
    /// region holds, so the one array serves every region (G5).
    /// </summary>
    private readonly byte[] _staged = new byte[PaintOrder.RegionSize * PaintOrder.RegionSize * 4];

    private CanvasDevice _device;

    /// <summary>The GPU bitmap a region is staged in on its way to a surface, made once per device.</summary>
    private CanvasBitmap? _staging;

    private MapGraphics(Compositor compositor)
    {
        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);

        // A geometry is the factory's, not the device's, so this one outlives a replacement.
        Nothing = new CompositionPath(CanvasGeometry.CreatePath(new CanvasPathBuilder(_device)));
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

    /// <summary>
    /// A path with nothing in it, for a geometry with nothing to draw. Never a null path: setting
    /// <see cref="CompositionPathGeometry.Path"/> to null ended the process with an access violation
    /// inside the setter on the Windows App SDK 1.8. Not at every such call, but once at the first of
    /// them and once many calls in, so no number of calls that survive makes it safe.
    /// </summary>
    public CompositionPath Nothing { get; }

    /// <summary>
    /// How many times the device has been replaced, so a map that was off the screen when it happened
    /// knows on its return that what it kept is gone.
    /// </summary>
    public int Generation { get; private set; }

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
    /// Let go of everything written into <paramref name="surface"/>, which shows nothing until it is
    /// written again. Trimming keeps what lies inside the rectangles it is given, so none are given.
    /// </summary>
    public static void Empty(CompositionVirtualDrawingSurface surface) => surface.Trim([]);

    /// <summary>
    /// Write <paramref name="region"/> of <paramref name="pixels"/>, a canvas <paramref name="width"/>
    /// pixels across, into the same place on <paramref name="surface"/>, and nothing else of it.
    /// </summary>
    public void Write(CompositionVirtualDrawingSurface surface, byte[] pixels, int width, CanvasRegion region)
    {
        PixelBuffer.CopyRegion(pixels, width, region, _staged);

        var device = _device;

        try
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
        }
        catch (Exception exception) when (device.IsDeviceLost(exception.HResult))
        {
            // The one failure to skip a region for: the device is replaced below, and everything is
            // drawn again on the new one. Any other failure is a defect, an invalid argument above all,
            // and is left to be raised. A pass can meet the same loss at several regions, and the
            // device it was met on is reported lost only once.
            if (device == _device)
            {
                device.RaiseDeviceLost();
            }
        }
    }

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
    /// A geometry round every outline, in the canvas's own pixels, for an outline drawn in the
    /// composition tree. <see cref="Nothing"/> where there is nothing to outline.
    ///
    /// <para>One geometry for all of them rather than one per outline, because the list view selects
    /// any number of rows at once and a shape apiece would be hundreds of them (G4).</para>
    /// </summary>
    public CompositionPath Trace(IReadOnlyList<ExploreOutline> outlines)
    {
        if (outlines.Count == 0)
        {
            return Nothing;
        }

        // Not disposed: making the geometry takes the builder over.
        var path = new CanvasPathBuilder(_device);

        foreach (var outline in outlines)
        {
            AddFigure(path, outline);
        }

        return new CompositionPath(CanvasGeometry.CreatePath(path));
    }

    /// <summary>
    /// A geometry over everything but <paramref name="outlines"/>, as far as any picture reaches, in
    /// the canvas's own pixels: what the map dims round a selection. <see cref="Nothing"/> where there
    /// is nothing to leave out, because then nothing is dimmed.
    /// </summary>
    public CompositionPath Around(IReadOnlyList<ExploreOutline> outlines)
    {
        if (Shapes(outlines) is not { } shapes)
        {
            return Nothing;
        }

        // As far as any picture reaches: a canvas is a 64th of its picture at the deepest zoom, and the
        // shape visual the dimming is drawn in cuts it to the picture.
        const float far = 1 << 20;

        var everything = CanvasGeometry.CreateRectangle(_device, -far, -far, 2 * far, 2 * far);

        return new CompositionPath(everything.CombineWith(shapes, Matrix3x2.Identity, CanvasGeometryCombine.Exclude));
    }

    /// <summary>
    /// Stripes across <paramref name="outlines"/> and nowhere else, in the canvas's own pixels: one
    /// stripe in every <paramref name="spacing"/>, <paramref name="width"/> wide, starting
    /// <paramref name="phase"/> along. <see cref="Nothing"/> where there is nothing to stripe.
    /// </summary>
    public CompositionPath Hatch(IReadOnlyList<ExploreOutline> outlines, float spacing, float width, float phase)
    {
        if (Shapes(outlines) is not { } shapes)
        {
            return Nothing;
        }

        // Upright stripes over a square round the shapes wide enough to cover them at any turn, turned
        // a quarter of the way round about its middle: a hatch reads as a hatch at any size of shape.
        var bounds = shapes.ComputeBounds();
        var side = (float)Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));
        var centre = new Vector2((float)(bounds.X + (bounds.Width / 2)), (float)(bounds.Y + (bounds.Height / 2)));
        var left = centre.X - (side / 2);
        var top = centre.Y - (side / 2);
        var stripes = new List<CanvasGeometry>();

        for (var x = left + phase; x < left + side; x += spacing)
        {
            stripes.Add(CanvasGeometry.CreateRectangle(_device, x, top, width, side));
        }

        var turned = CanvasGeometry.CreateGroup(_device, [.. stripes], CanvasFilledRegionDetermination.Winding)
            .Transform(Matrix3x2.CreateRotation(MathF.PI / 4, centre));

        return new CompositionPath(turned.CombineWith(shapes, Matrix3x2.Identity, CanvasGeometryCombine.Intersect));
    }

    /// <summary>
    /// The shapes <paramref name="outlines"/> bound, filled, as one geometry, or null where there are
    /// none.
    ///
    /// <para>A node's boundaries are filled together, alternately, which is how a ring keeps its hole.
    /// Each is then outlined, which leaves a geometry filled the same whichever rule fills it, and the
    /// nodes are grouped under the rule that fills wherever any of them does: their union, so a folder
    /// picked with a file inside it is one shape rather than a folder with the file cut out.</para>
    /// </summary>
    private CanvasGeometry? Shapes(IReadOnlyList<ExploreOutline> outlines)
    {
        var nodes = new List<CanvasGeometry>();
        var i = 0;

        while (i < outlines.Count)
        {
            // Not disposed: making the geometry takes the builder over.
            var path = new CanvasPathBuilder(_device);
            path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);

            var node = outlines[i].Node;
            var figures = 0;

            // A node's boundaries come back one after another: see ExploreOutline.
            for (; i < outlines.Count && outlines[i].Node == node; i++)
            {
                figures += AddFigure(path, outlines[i]) ? 1 : 0;
            }

            if (figures > 0)
            {
                nodes.Add(CanvasGeometry.CreatePath(path).Outline());
            }
        }

        return nodes.Count == 0
            ? null
            : CanvasGeometry.CreateGroup(_device, [.. nodes], CanvasFilledRegionDetermination.Winding);
    }

    /// <summary>
    /// Add <paramref name="outline"/> to <paramref name="path"/> as one closed figure, and say whether
    /// there was anything to add. The one way a boundary becomes a figure, so the line round a shape
    /// and the area it fills are the same shape.
    /// </summary>
    private static bool AddFigure(CanvasPathBuilder path, ExploreOutline outline)
    {
        var points = outline.Points;

        if (points.Count == 0)
        {
            return false;
        }

        path.BeginFigure(points[0].X, points[0].Y);

        for (var i = 1; i < points.Count; i++)
        {
            path.AddLine(points[i].X, points[i].Y);
        }

        path.EndFigure(CanvasFigureLoop.Closed);

        return true;
    }

    /// <summary>
    /// The device was lost: replace it under the compositor's graphics device, which raises
    /// <see cref="Replaced"/> once every surface is on the new one.
    /// </summary>
    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        sender.DeviceLost -= OnDeviceLost;

        _staging = null;
        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        CanvasComposition.SetCanvasDevice(_graphics, _device);
    }
}
