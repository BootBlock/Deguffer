using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// The pages every name on a map is drawn into, a rectangle each, made on the map's device (see
/// <see cref="CompositionDevice"/>).
///
/// <para>A few large pages rather than a surface per name, so the compositor draws every name on the
/// map from a few textures, rather than a texture of each name's own on every frame of a pan.</para>
///
/// <para>Each page is a plain surface (<see cref="MapGraphics.PlainSurface"/>), never a virtual one.
/// Every name shows its rectangle through a brush of its own, and the compositor draws a virtual
/// surface through each of those brushes far more slowly than a plain one: a pan of a maximised map
/// of Classic names costs it about 300 ms a frame from one virtual sheet, and about 13 ms from a plain
/// one. A page is made only when the names on the ones before it leave no room, so a map of few names
/// holds one page.</para>
///
/// <para>The device replaced leaves a surface that takes a write without an error and shows nothing of
/// it (see <see cref="CompositionDevice.Replaced"/>), so the pages are made again on the new device the
/// next time names are drawn, and every name is drawn into them again: see <see cref="Prepare"/>.</para>
/// </summary>
internal sealed class NameSheet
{
    /// <summary>
    /// How wide a page is, in pixels, and so the widest a name can be drawn. Half the largest texture a
    /// Direct3D 10 device takes (8,192), because a plain surface at or past a device's limit is refused.
    /// </summary>
    internal const int PageWidth = 4096;

    /// <summary>How tall a page is, in pixels: 32 MB of texture with <see cref="PageWidth"/>.</summary>
    internal const int PageHeight = 2048;

    /// <summary>The most pages there can be: as much room as one surface 8,192 pixels square.</summary>
    internal const int MostPages = 8;

    private readonly MapGraphics _graphics;

    private readonly List<Page> _pages = [];

    /// <summary>
    /// The device the pages are made on, and the device's generation then, so a device replaced since
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

    /// <summary>The widest a name's rectangle can be, in pixels.</summary>
    public int Width => PageWidth - SheetShelves.Gutter;

    /// <summary>
    /// Draw on the device drawn through now. Where it is another than the pages were made on, every
    /// page is let go of and every rectangle handed out before is void.
    /// </summary>
    /// <returns>Whether the sheet is new: every name has to take a rectangle of it again.</returns>
    public bool Prepare()
    {
        var device = _graphics.Device;

        if (_madeOn == (device.Current, device.Generation))
        {
            return false;
        }

        _madeOn = (device.Current, device.Generation);
        Clear();

        return true;
    }

    /// <summary>
    /// Empty the sheet: every rectangle is void, and every page is let go of. For a drawing whose names
    /// no longer fit around those left over, and for a map showing nothing.
    /// </summary>
    public void Clear()
    {
        foreach (var page in _pages)
        {
            page.Surface.Dispose();
        }

        _pages.Clear();
        Epoch++;
    }

    /// <summary>
    /// A rectangle <paramref name="width"/> by <paramref name="height"/> pixels on the first page with
    /// room for it, on a page made for it where none has, or null where the sheet is full.
    /// </summary>
    public SheetPlace? Take(int width, int height)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Shelves.Take(width, height) is { } region)
            {
                return new SheetPlace(i, region);
            }
        }

        if (_pages.Count == MostPages)
        {
            return null;
        }

        var shelves = new SheetShelves(PageWidth, PageHeight);

        // A rectangle no page could hold makes no page.
        if (shelves.Take(width, height) is not { } taken)
        {
            return null;
        }

        _pages.Add(new Page(NewPage(), shelves));

        return new SheetPlace(_pages.Count - 1, taken);
    }

    /// <summary>Take back <paramref name="place"/>, taken from this sheet since its <see cref="Epoch"/> last changed.</summary>
    public void Give(SheetPlace place) => _pages[place.Page].Shelves.Give(place.Region);

    /// <summary>The surface of the page <paramref name="place"/> is on, which shows it.</summary>
    public ICompositionSurface SurfaceOf(SheetPlace place) => _pages[place.Page].Surface;

    /// <summary>
    /// A drawing session over <paramref name="place"/>, measured in device-independent pixels from
    /// the rectangle's corner at <paramref name="scale"/> pixels to each, with the rectangle and a pixel
    /// all round it cleared.
    ///
    /// <para>A name moved off the pixel grid samples a pixel past each edge. A rectangle taken back holds
    /// what its last name left, so the ring is cleared with the rectangle. The pixel to the right and
    /// below is the rectangle's own gutter, and the pixel to the left and above is another rectangle's
    /// gutter or room no rectangle reaches (see <see cref="SheetShelves"/>), so clearing it never touches
    /// another name.</para>
    /// </summary>
    public CanvasDrawingSession Open(SheetPlace place, double scale)
    {
        var (cleared, corner) = Ring(place.Region);

        var session = CanvasComposition.CreateDrawingSession(_pages[place.Page].Surface, cleared, (float)(96 * scale));

        session.Clear(Microsoft.UI.Colors.Transparent);
        session.Transform = Matrix3x2.CreateTranslation((float)(corner.X / scale), (float)(corner.Y / scale));

        return session;
    }

    /// <summary>
    /// <paramref name="region"/> and the pixel all round it, kept on the page, and where the region's
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

    /// <summary>A page on the device drawn through now, cleared whole, as the first write into a plain surface has to be.</summary>
    private CompositionDrawingSurface NewPage()
    {
        var surface = _graphics.PlainSurface(null, PageWidth, PageHeight);

        using (var session = CanvasComposition.CreateDrawingSession(surface))
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
        }

        return surface;
    }

    /// <summary>One page: its surface, and where the names on it are.</summary>
    private sealed record Page(CompositionDrawingSurface Surface, SheetShelves Shelves);
}

/// <summary>Where a name is drawn: a rectangle of one page of the <see cref="NameSheet"/>.</summary>
internal readonly record struct SheetPlace(int Page, SheetRegion Region);
