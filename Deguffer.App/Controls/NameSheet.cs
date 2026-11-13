using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics;

namespace Deguffer.App.Controls;

/// <summary>
/// The one surface every name on a map is drawn into, a rectangle each, made on the map's device (see
/// <see cref="CompositionDevice"/>).
///
/// <para>One surface rather than one per name, so the compositor draws two thousand names from one
/// texture: a surface of each name's own, as each name had in its own image, was a texture and a brush
/// for the compositor to draw on every frame of a pan for each name on the map.</para>
///
/// <para>A virtual surface, so only the part of it names are drawn into takes memory, and a name can
/// be drawn into its own rectangle without the whole surface being drawn first, as a plain surface's
/// first write has to be. Wide enough for a name across the widest screen at any text size.</para>
///
/// <para>The device replaced leaves a surface that takes a write without an error and shows nothing of
/// it (see <see cref="CompositionDevice.Replaced"/>), so the sheet is made again on the new device the
/// next time names are drawn, and every name is drawn into it again: see <see cref="Prepare"/>.</para>
/// </summary>
internal sealed class NameSheet
{
    /// <summary>How wide and how tall the sheet is, in pixels: wider than any screen a name lies across.</summary>
    private const int Side = 8192;

    private readonly CompositionDevice _device;

    private readonly SheetShelves _shelves = new(Side, Side);

    private CompositionVirtualDrawingSurface? _surface;

    /// <summary>
    /// The device the sheet was made on, and the device's generation then, so a device replaced since
    /// is noticed: at once where this process replaced it, and at the generation's change where the
    /// compositor did.
    /// </summary>
    private (CanvasDevice Device, int Generation)? _madeOn;

    /// <summary>A sheet drawn through the map's <paramref name="device"/>.</summary>
    public NameSheet(CompositionDevice device) => _device = device;

    /// <summary>
    /// Which sheet this is: a number that changes whenever every rectangle handed out before is void,
    /// because the sheet was emptied or made again. A name's rectangle is its own only while the sheet
    /// is the one it was taken from.
    /// </summary>
    public int Epoch { get; private set; }

    /// <summary>The surface names are drawn into, or null before the first <see cref="Prepare"/>.</summary>
    public ICompositionSurface? Surface => _surface;

    /// <summary>The widest a name's rectangle can be, in pixels.</summary>
    public int Width => _shelves.Width;

    /// <summary>
    /// Make the sheet on the device drawn through now, where it is not already. Every rectangle handed
    /// out before is void, and every brush has to show the new <see cref="Surface"/>.
    /// </summary>
    /// <returns>Whether the sheet was made anew.</returns>
    public bool Prepare()
    {
        if (_surface is not null && _madeOn == (_device.Current, _device.Generation))
        {
            return false;
        }

        _surface?.Dispose();
        _surface = _device.Graphics.CreateVirtualDrawingSurface(
            new SizeInt32(Side, Side),
            CompositionDevice.Format,
            CompositionDevice.Alpha);
        _madeOn = (_device.Current, _device.Generation);

        _shelves.Clear();
        Epoch++;

        return true;
    }

    /// <summary>
    /// Empty the sheet: every rectangle is void, and what was drawn into them is let go of. For a
    /// drawing whose names no longer fit around those left over, and for a map showing nothing.
    /// </summary>
    public void Clear()
    {
        _shelves.Clear();
        Epoch++;

        if (_surface is not null)
        {
            MapGraphics.Empty(_surface);
        }
    }

    /// <summary>A rectangle <paramref name="width"/> by <paramref name="height"/> pixels, or null where the sheet is full.</summary>
    public SheetRegion? Take(int width, int height) => _shelves.Take(width, height);

    /// <summary>Take back <paramref name="region"/>, taken from this sheet since its <see cref="Epoch"/> last changed.</summary>
    public void Give(SheetRegion region) => _shelves.Give(region);

    /// <summary>
    /// A drawing session over <paramref name="region"/> alone, cleared, measured in device-independent
    /// pixels from the region's corner at <paramref name="scale"/> pixels to each. What a session over
    /// part of a surface finds there is undefined, so it is cleared before anything is drawn.
    /// </summary>
    public CanvasDrawingSession Open(SheetRegion region, double scale)
    {
        var session = CanvasComposition.CreateDrawingSession(
            _surface!,
            new Rect(region.X, region.Y, region.Width, region.Height),
            (float)(96 * scale));

        session.Clear(Microsoft.UI.Colors.Transparent);

        return session;
    }
}
