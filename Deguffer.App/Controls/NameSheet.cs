using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// The one surface every name on a map is drawn into, a rectangle each, made on the map's device (see
/// <see cref="CompositionDevice"/>).
///
/// <para>One surface rather than one per name, so the compositor draws every name on the map from one
/// texture, rather than a texture of each name's own on every frame of a pan.</para>
///
/// <para>A virtual surface (see <see cref="MapGraphics.CreateSurface"/>), so only the part of it names
/// are drawn into takes memory, and a name can be drawn into its own rectangle without the whole
/// surface being drawn first.</para>
///
/// <para>The device replaced leaves a surface that takes a write without an error and shows nothing of
/// it (see <see cref="CompositionDevice.Replaced"/>), so the sheet is made again on the new device the
/// next time names are drawn, and every name is drawn into it again: see <see cref="Prepare"/>.</para>
/// </summary>
internal sealed class NameSheet
{
    /// <summary>
    /// How wide and how tall the sheet is, in pixels: as wide as an 8K screen is across, and as wide as
    /// a name lying across one can be.
    /// </summary>
    private const int Side = 8192;

    private readonly MapGraphics _graphics;

    private readonly SheetShelves _shelves = new(Side, Side);

    private CompositionVirtualDrawingSurface? _surface;

    /// <summary>
    /// The device the sheet was made on, and the device's generation then, so a device replaced since
    /// is noticed: at once where this process replaced it, and at the generation's change where the
    /// compositor did.
    /// </summary>
    private (CanvasDevice Device, int Generation)? _madeOn;

    /// <summary>A sheet drawn through the map's <paramref name="graphics"/>.</summary>
    public NameSheet(MapGraphics graphics) => _graphics = graphics;

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
        var device = _graphics.Device;

        if (_surface is not null && _madeOn == (device.Current, device.Generation))
        {
            return false;
        }

        _surface?.Dispose();
        _surface = _graphics.CreateSurface(Side, Side);
        _madeOn = (device.Current, device.Generation);

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
    /// A drawing session over <paramref name="region"/>, measured in device-independent pixels from
    /// the region's corner at <paramref name="scale"/> pixels to each, with the region and a pixel all
    /// round it cleared.
    ///
    /// <para>A name moved off the pixel grid samples a pixel past each edge. What a session over part
    /// of a surface finds there is undefined, and a rectangle taken back holds what its last name left,
    /// so the ring is cleared with the rectangle. The pixel to the right and below is the rectangle's
    /// own gutter, and the pixel to the left and above is another rectangle's gutter or room no
    /// rectangle reaches (see <see cref="SheetShelves"/>), so clearing it never touches another name.</para>
    /// </summary>
    public CanvasDrawingSession Open(SheetRegion region, double scale)
    {
        var surface = _surface ?? throw new InvalidOperationException("The names' sheet is drawn into before it is made.");
        var (cleared, corner) = Ring(region);

        var session = CanvasComposition.CreateDrawingSession(surface, cleared, (float)(96 * scale));

        session.Clear(Microsoft.UI.Colors.Transparent);
        session.Transform = Matrix3x2.CreateTranslation((float)(corner.X / scale), (float)(corner.Y / scale));

        return session;
    }

    /// <summary>
    /// <paramref name="region"/> and the pixel all round it, kept on the sheet, and where the region's
    /// corner is within that, in pixels.
    /// </summary>
    internal static (Rect Cleared, Point Corner) Ring(SheetRegion region)
    {
        var left = Math.Min(region.X, SheetShelves.Gutter);
        var above = Math.Min(region.Y, SheetShelves.Gutter);

        return (
            new Rect(region.X - left, region.Y - above, region.Width + left + SheetShelves.Gutter, region.Height + above + SheetShelves.Gutter),
            new Point(left, above));
    }
}
