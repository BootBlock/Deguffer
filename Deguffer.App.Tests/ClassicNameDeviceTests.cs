using Deguffer.App.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// A name in SpaceMonger's lettering is drawn through the map's own device, never a second one, and is
/// drawn again on the device that replaces it.
///
/// <para>Into a bitmap rather than the window's image source, which a test host cannot make. On a
/// compositor of each test's own, so a device lost here is not the one the other tests draw through.</para>
/// </summary>
public sealed class ClassicNameDeviceTests
{
    private static readonly Color Ink = Color.FromArgb(255, 0, 0, 0);

    [Fact]
    public void ANameIsDrawnThroughTheMapsDevice()
    {
        var (onTheMapsDevice, inked) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var device = CompositionDevice.For(compositor);
            var name = new ClassicName<CanvasRenderTarget>(device, BitmapCanvas.Instance);

            Draw(name);

            return (name.Image!.Device == device.Current, IsInked(name.Image));
        });

        Assert.True(onTheMapsDevice);
        Assert.True(inked);
    }

    /// <summary>
    /// The name is unchanged, so only the replacement can make it draw again: an image kept from the old
    /// device would show nothing of the new one, and keep the old device alive.
    /// </summary>
    [Fact]
    public void AReplacedDeviceDrawsTheNameAgainOnTheOneThatReplacesIt()
    {
        var (remade, onTheNewDevice, offTheOldOne, inked) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var device = CompositionDevice.For(compositor);
            var name = new ClassicName<CanvasRenderTarget>(device, BitmapCanvas.Instance);

            Draw(name);
            var before = name.Image!;
            var lost = device.Current;

            // As Win2D reports a loss, which it does only for a device that is lost.
            device.OnDeviceLost(lost, null);

            Draw(name);
            var after = name.Image!;

            return (!ReferenceEquals(after, before), after.Device == device.Current, after.Device != lost, IsInked(after));
        });

        Assert.True(remade);
        Assert.True(onTheNewDevice);
        Assert.True(offTheOldOne);
        Assert.True(inked);
    }

    [Fact]
    public void AnUnchangedNameKeepsItsImage()
    {
        var kept = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var name = new ClassicName<CanvasRenderTarget>(CompositionDevice.For(compositor), BitmapCanvas.Instance);

            Draw(name);
            var before = name.Image;
            Draw(name);

            return ReferenceEquals(name.Image, before);
        });

        Assert.True(kept);
    }

    private static void Draw(ClassicName<CanvasRenderTarget> name) =>
        name.Draw("Downloads", room: 200, centred: true, Ink, spacing: 13, scale: 1);

    private static bool IsInked(CanvasRenderTarget image)
    {
        var pixels = image.GetPixelBytes();

        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            if (pixels[alpha] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A bitmap the test reads the drawn name back from.</summary>
    private sealed class BitmapCanvas : ILabelCanvas<CanvasRenderTarget>
    {
        public static readonly BitmapCanvas Instance = new();

        public CanvasRenderTarget Make(CanvasDevice device, float width, float height, float dpi) =>
            new(device, width, height, dpi);

        public CanvasDrawingSession Open(CanvasRenderTarget image)
        {
            var session = image.CreateDrawingSession();
            session.Clear(Color.FromArgb(0, 0, 0, 0));

            return session;
        }
    }
}
