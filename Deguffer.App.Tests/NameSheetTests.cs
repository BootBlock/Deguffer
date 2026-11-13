using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// Every name on a map is drawn into one surface made on the map's own device: a name written again
/// unchanged is not drawn again, and the device replaced makes the sheet again and draws every name
/// into it.
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
    public void TheSheetIsMadeOnceOnTheMapsDevice()
    {
        var (first, again, surface) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var sheet = new NameSheet(MapGraphics.For(compositor));

            var first = sheet.Prepare();
            var made = sheet.Surface;
            var again = sheet.Prepare();

            return (first, again, made is not null && ReferenceEquals(made, sheet.Surface));
        });

        Assert.True(first);
        Assert.False(again);
        Assert.True(surface);
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
            var name = new NameSprite(compositor, sheet);

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
            var before = sheet.Surface;
            var name = new NameSprite(compositor, sheet);

            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            name.Draw(sheet);

            // As Win2D reports a loss, which it does only for a device that is lost.
            device.OnDeviceLost(device.Current, null);

            var remade = sheet.Prepare();

            if (remade)
            {
                name.Renew(sheet);
            }

            var forgotten = !name.Text.IsDrawn;

            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            name.Draw(sheet);

            return (remade, !ReferenceEquals(before, sheet.Surface), forgotten, name.Text.IsDrawn);
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

            var name = new NameSprite(compositor, sheet);
            Write(name, device, scale: 1);
            name.Fit(sheet, 1);
            var (width, height) = ((int)name.Visual.Size.X, (int)name.Visual.Size.Y);

            name.Collapse(sheet);

            return (!name.Visual.IsVisible, sheet.Take(width, height));
        });

        Assert.True(hidden);
        Assert.Equal(0, next?.X);
        Assert.Equal(0, next?.Y);
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

    private static void Write(NameSprite name, CompositionDevice device, double scale) =>
        name.Text.Write(device.Current, Formats, new NameWriting("Downloads", MapLettering.Classic, 200, true, Ink, (float)(13 / scale), 1));
}
