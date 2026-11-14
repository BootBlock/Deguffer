using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// Every name on a map is drawn into a few plain pages made on the map's own device: a page is made
/// only when the others have no room, a name written again unchanged is not drawn again, and the
/// device replaced makes the sheet again and draws every name into it.
///
/// <para>On a compositor of each test's own, so a device lost here is not the one the other tests draw
/// through.</para>
/// </summary>
public sealed class NameSheetTests
{
    private static readonly Color Ink = Color.FromArgb(255, 0, 0, 0);

    /// <summary>One set of formats, as a map has, so a name written again is written in the same type.</summary>
    private static readonly NameFormats Formats = new();

    [Fact]
    public void TheSheetIsPreparedOnceOnTheMapsDevice()
    {
        var (first, again, kept) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var sheet = new NameSheet(MapGraphics.For(compositor));

            var first = sheet.Prepare();
            var place = sheet.Take(40, 12)!.Value;
            var page = sheet.SurfaceOf(place);
            var again = sheet.Prepare();

            return (first, again, ReferenceEquals(page, sheet.SurfaceOf(place)));
        });

        Assert.True(first);
        Assert.False(again);
        Assert.True(kept);
    }

    /// <summary>
    /// Every name shows its own rectangle through a brush of its own, and the compositor draws a virtual
    /// surface through each such brush far more slowly than a plain one: a pan of a maximised map of
    /// Classic names costs about 300 ms a frame from one virtual sheet, and about 13 ms from a plain one.
    /// </summary>
    [Fact]
    public void NoPageIsAVirtualSurface()
    {
        var isVirtual = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var sheet = new NameSheet(MapGraphics.For(compositor));
            sheet.Prepare();

            return sheet.SurfaceOf(sheet.Take(40, 12)!.Value) is CompositionVirtualDrawingSurface;
        });

        Assert.False(isVirtual);
    }

    /// <summary>
    /// A page is made only when the ones before it have no room, up to the most there can be, and
    /// emptying the sheet lets every page go, so the next name starts a page made anew.
    /// </summary>
    [Fact]
    public void APageIsMadeOnlyWhenTheOthersAreFull()
    {
        var (first, small, second, last, full, afterClear, madeAnew) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var sheet = new NameSheet(MapGraphics.For(compositor));
            sheet.Prepare();

            var first = sheet.Take(40, 12)!.Value;
            var small = sheet.Take(40, 12)!.Value;
            var second = sheet.Take(WholePage.Width, WholePage.Height)!.Value;
            var last = second;

            // Pages 0 and 1 are in use: fill the rest, one whole page each.
            for (var i = 2; i < NameSheet.MostPages; i++)
            {
                last = sheet.Take(WholePage.Width, WholePage.Height)!.Value;
            }

            var full = sheet.Take(WholePage.Width, WholePage.Height);
            var firstPage = sheet.SurfaceOf(first);
            sheet.Clear();
            var afterClear = sheet.Take(40, 12)!.Value;

            return (first.Page, small.Page, second.Page, last.Page, full, afterClear.Page, !ReferenceEquals(firstPage, sheet.SurfaceOf(afterClear)));
        });

        Assert.Equal(0, first);
        Assert.Equal(0, small);
        Assert.Equal(1, second);
        Assert.Equal(NameSheet.MostPages - 1, last);
        Assert.Null(full);
        Assert.Equal(0, afterClear);
        Assert.True(madeAnew);
    }

    /// <summary>A name's brush shows the page its rectangle is on, not the first page.</summary>
    [Fact]
    public void ANameShowsThePageItsRectangleIsOn()
    {
        var (onSecond, notFirst) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var graphics = MapGraphics.For(compositor);
            var sheet = new NameSheet(graphics);
            sheet.Prepare();

            // The first page left with no room for a name.
            var filler = sheet.Take(WholePage.Width, WholePage.Height)!.Value;

            var name = new NameSprite(compositor);
            Write(name, graphics.Device, scale: 1);
            name.Fit(sheet, 1);

            var second = filler with { Page = 1 };

            return (ReferenceEquals(Shown(name), sheet.SurfaceOf(second)), !ReferenceEquals(Shown(name), sheet.SurfaceOf(filler)));
        });

        Assert.True(onSecond);
        Assert.True(notFirst);
    }

    /// <summary>A name written again unchanged keeps its rectangle and what was drawn there.</summary>
    [Fact]
    public void AnUnchangedNameKeepsItsRectangleAndIsNotDrawnAgain()
    {
        var (drawn, keptDrawing, size) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var graphics = MapGraphics.For(compositor);
            var device = graphics.Device;
            var sheet = new NameSheet(graphics);
            sheet.Prepare();
            var name = new NameSprite(compositor);

            Write(name, device, scale: 1.5);
            name.Fit(sheet, 1.5);
            name.Draw(sheet);
            var drawn = name.Text.IsDrawn;

            Write(name, device, scale: 1.5);
            name.Fit(sheet, 1.5);

            return (drawn, name.Text.IsDrawn, name.Visual.Size);
        });

        Assert.True(drawn);
        Assert.True(keptDrawing);

        // The rectangle's whole pixels, shown at the device-independent size they cover.
        Assert.Equal(size.X * 1.5f, MathF.Round(size.X * 1.5f), 3);
        Assert.True(size.X > 0 && size.Y > 0);
    }

    /// <summary>
    /// The name is unchanged, so only the replacement can make it draw again: a surface kept from the
    /// old device takes a write and shows nothing of it.
    /// </summary>
    [Fact]
    public void AReplacedDeviceMakesTheSheetAgainAndDrawsTheNameIntoIt()
    {
        var (remade, newSurface, forgotten, drawnAgain) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var graphics = MapGraphics.For(compositor);
            var device = graphics.Device;
            var sheet = new NameSheet(graphics);
            sheet.Prepare();
            var name = new NameSprite(compositor);

            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            name.Draw(sheet);
            var before = Shown(name);

            // As Win2D reports a loss, which it does only for a device that is lost.
            device.OnDeviceLost(device.Current, null);

            var remade = sheet.Prepare();

            if (remade)
            {
                name.Renew();
            }

            var forgotten = !name.Text.IsDrawn;

            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            name.Draw(sheet);

            return (remade, Shown(name) is { } after && !ReferenceEquals(before, after), forgotten, name.Text.IsDrawn);
        });

        Assert.True(remade);
        Assert.True(newSurface);
        Assert.True(forgotten);
        Assert.True(drawnAgain);
    }

    /// <summary>A name put away gives its rectangle back, so the next name of that size takes the same one.</summary>
    [Fact]
    public void ANamePutAwayGivesItsRectangleBack()
    {
        var (hidden, next) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var graphics = MapGraphics.For(compositor);
            var device = graphics.Device;
            var sheet = new NameSheet(graphics);
            sheet.Prepare();

            var name = new NameSprite(compositor);
            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            var (width, height) = ((int)name.Visual.Size.X, (int)name.Visual.Size.Y);

            name.Collapse(sheet);

            return (!name.Visual.IsVisible, sheet.Take(width, height));
        });

        Assert.True(hidden);
        Assert.Equal(0, next?.Page);
        Assert.Equal(0, next?.Region.X);
        Assert.Equal(0, next?.Region.Y);
    }

    /// <summary>
    /// A name is drawn with a pixel cleared all round it, which a name moved off the pixel grid samples,
    /// and only as far as the sheet's edge where it lies against one.
    /// </summary>
    [Fact]
    public void ANameIsClearedWithAPixelAllRoundIt()
    {
        var inside = NameSheet.Ring(new SheetRegion(10, 20, 30, 12));
        var corner = NameSheet.Ring(new SheetRegion(0, 0, 30, 12));

        Assert.Equal(new Windows.Foundation.Rect(9, 19, 32, 14), inside.Cleared);
        Assert.Equal(new Windows.Foundation.Point(1, 1), inside.Corner);
        Assert.Equal(new Windows.Foundation.Rect(0, 0, 31, 13), corner.Cleared);
        Assert.Equal(new Windows.Foundation.Point(0, 0), corner.Corner);
    }

    /// <summary>The largest rectangle a page holds, so each one taken takes a page of its own.</summary>
    private static (int Width, int Height) WholePage => (NameSheet.PageWidth - SheetShelves.Gutter, NameSheet.PageHeight - SheetShelves.Gutter);

    /// <summary>The page the brush of <paramref name="name"/> shows.</summary>
    private static ICompositionSurface? Shown(NameSprite name) => ((CompositionSurfaceBrush)name.Visual.Brush).Surface;

    private static void Write(NameSprite name, CompositionDevice device, double scale) =>
        name.Text.Write(device.Current, Formats, new NameWriting("Downloads", MapLettering.Classic, 200, true, Ink, (float)(13 / scale), 1));
}
