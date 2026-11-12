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
            var name = new ClassicName<CanvasRenderTarget>(device, new ClassicFormats(), BitmapCanvas.Instance);

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
            var name = new ClassicName<CanvasRenderTarget>(device, new ClassicFormats(), BitmapCanvas.Instance);

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
            var name = new ClassicName<CanvasRenderTarget>(CompositionDevice.For(compositor), new ClassicFormats(), BitmapCanvas.Instance);

            Draw(name);
            var before = name.Image;
            Draw(name);

            return ReferenceEquals(name.Image, before);
        });

        Assert.True(kept);
    }

    /// <summary>A name's measurement is kept while its text is unchanged, and only while it is.</summary>
    [Fact]
    public void ANameWhoseTextChangesIsMeasuredAgain()
    {
        var (longer, shorter) = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var name = new ClassicName<CanvasRenderTarget>(CompositionDevice.For(compositor), new ClassicFormats(), BitmapCanvas.Instance);

            var longer = name.Draw("Downloads", room: 200, centred: true, Ink, spacing: 13, scale: 1);
            var shorter = name.Draw("D", room: 200, centred: true, Ink, spacing: 13, scale: 1);

            return (longer.Width, shorter.Width);
        });

        Assert.True(shorter < longer, $"\"D\" was {shorter} wide and \"Downloads\" {longer}.");
    }

    /// <summary>
    /// Each line of a centred name is centred in the image, and each line of one that is not starts at
    /// its left. The format is shared by every name, so the alignment is the name's own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShortLineIsCentredOnlyInACentredName(bool centred)
    {
        var start = CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();
            var name = new ClassicName<CanvasRenderTarget>(CompositionDevice.For(compositor), new ClassicFormats(), BitmapCanvas.Instance);

            name.Draw("Downloads\nD", room: 200, centred, Ink, spacing: 13, scale: 1);

            return (FirstInkedColumn(name.Image!, fromRow: 13, toRow: 26), (int)name.Image!.SizeInPixels.Width);
        });

        Assert.True(start.Item1 < start.Item2, "The second line has no ink.");

        if (centred)
        {
            Assert.True(start.Item1 > start.Item2 / 4, $"The second line started at {start.Item1} of {start.Item2}.");
        }
        else
        {
            Assert.True(start.Item1 < 3, $"The second line started at {start.Item1} of {start.Item2}.");
        }
    }

    [Fact]
    public void NamesOfOneSpacingShareOneFormat()
    {
        var formats = new ClassicFormats();

        Assert.Same(formats.For(13), formats.For(13));
        Assert.NotSame(formats.For(13), formats.For(20));
        Assert.Equal(20, formats.For(20).LineSpacing);
    }

    private static void Draw(ClassicName<CanvasRenderTarget> name) =>
        name.Draw("Downloads", room: 200, centred: true, Ink, spacing: 13, scale: 1);

    /// <summary>The leftmost column with any ink in rows <paramref name="fromRow"/> up to <paramref name="toRow"/>, or the image's width where there is none.</summary>
    private static int FirstInkedColumn(CanvasRenderTarget image, int fromRow, int toRow)
    {
        var pixels = image.GetPixelBytes();
        var width = (int)image.SizeInPixels.Width;
        var height = Math.Min(toRow, (int)image.SizeInPixels.Height);
        var first = width;

        for (var row = fromRow; row < height; row++)
        {
            for (var column = 0; column < first; column++)
            {
                if (pixels[(((row * width) + column) * 4) + 3] != 0)
                {
                    first = column;
                }
            }
        }

        return first;
    }

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
