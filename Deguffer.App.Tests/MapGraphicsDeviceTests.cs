using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// The graphics and the device a map draws through are one for each compositor, and a lost device
/// takes what was made on it with it.
///
/// <para>On a compositor of each test's own, made on the compositor's thread, so a device lost here is
/// not the one the other tests draw through.</para>
/// </summary>
public sealed class MapGraphicsDeviceTests
{
    [Fact]
    public void EachCompositorHasGraphicsOfItsOwn()
    {
        var (same, other, devices) = CompositionThread.Run(() =>
        {
            using var first = new Compositor();
            using var second = new Compositor();

            var graphics = MapGraphics.For(first);

            return (
                ReferenceEquals(graphics, MapGraphics.For(first)),
                ReferenceEquals(graphics, MapGraphics.For(second)),
                ReferenceEquals(CompositionDevice.For(first), CompositionDevice.For(second)));
        });

        Assert.True(same);
        Assert.False(other);
        Assert.False(devices);
    }

    /// <summary>
    /// The bitmap a region is staged in is disposed when its device is lost, and a write on the device
    /// that replaces it stages through a new one.
    /// </summary>
    [Fact]
    public void ALostDeviceDisposesTheStagingBitmap()
    {
        var replaced = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var graphics = MapGraphics.For(compositor);

            Write(graphics);
            var staging = graphics.Staging!;

            // As Win2D reports a loss, which it does only for a device that is lost.
            graphics.Device.OnDeviceLost(graphics.Device.Current, null);

            Assert.Throws<ObjectDisposedException>(() => staging.SizeInPixels);

            Write(graphics);

            return graphics.Staging is { } next && !ReferenceEquals(next, staging);
        });

        Assert.True(replaced);
    }

    private static void Write(MapGraphics graphics)
    {
        const int Side = 4;

        var surface = graphics.PlainSurface(null, Side, Side);

        graphics.Write(surface, new byte[PixelBuffer.LengthFor(Side, Side)], Side, new CanvasRegion(0, 0, Side, Side));
    }
}
