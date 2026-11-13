using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// A name's text is measured and drawn in its lettering: SpaceMonger's type pixel for pixel and
/// centred line by line, the application's smoothed and cut short to its room, and either measured
/// again only when its text changes.
///
/// <para>Drawn into a bitmap the test reads back, on the map's device for a compositor of each test's
/// own, rather than into the names' sheet, whose pixels a test cannot read.</para>
/// </summary>
public sealed class NameTextTests
{
    private static readonly Color Ink = Color.FromArgb(255, 0, 0, 0);

    [Theory]
    [InlineData(MapLettering.Classic)]
    [InlineData(MapLettering.Standard)]
    public void ANameWhoseTextChangesIsMeasuredAgain(MapLettering lettering)
    {
        var (longer, shorter) = OnDevice(device =>
        {
            var text = new NameText();
            var formats = new NameFormats();

            var longer = Write(text, device, formats, "Downloads", lettering);
            var shorter = Write(text, device, formats, "D", lettering);

            return (longer.Width, shorter.Width);
        });

        Assert.True(shorter < longer, $"\"D\" was {shorter} wide and \"Downloads\" {longer}.");
    }

    /// <summary>
    /// What a name shows is drawn once, and drawn again only once it is written with something else to
    /// show or where it was drawn is lost: most names are written unchanged from one drawing to the next.
    /// </summary>
    [Fact]
    public void AnUnchangedNameIsNotDrawnAgain()
    {
        var (beforeDrawing, drawn, rewritten, changed, forgotten) = OnDevice(device =>
        {
            var text = new NameText();
            var formats = new NameFormats();
            using var bitmap = new CanvasRenderTarget(device, 200, 40, 96);

            Write(text, device, formats, "Downloads", MapLettering.Classic);
            var beforeDrawing = text.IsDrawn;

            using (var session = bitmap.CreateDrawingSession())
            {
                text.Draw(session);
            }

            var drawn = text.IsDrawn;

            Write(text, device, formats, "Downloads", MapLettering.Classic);
            var rewritten = text.IsDrawn;

            Write(text, device, formats, "Documents", MapLettering.Classic);
            var changed = text.IsDrawn;

            using (var session = bitmap.CreateDrawingSession())
            {
                text.Draw(session);
            }

            text.Forget();

            return (beforeDrawing, drawn, rewritten, changed, text.IsDrawn);
        });

        Assert.False(beforeDrawing);
        Assert.True(drawn);
        Assert.True(rewritten);
        Assert.False(changed);
        Assert.False(forgotten);
    }

    /// <summary>
    /// SpaceMonger's type is drawn without smoothing, every pixel inked or not, and the application's is
    /// smoothed, as a text block draws it.
    /// </summary>
    [Theory]
    [InlineData(MapLettering.Classic, false)]
    [InlineData(MapLettering.Standard, true)]
    public void OnlyTheApplicationsTypeIsSmoothed(MapLettering lettering, bool smoothed)
    {
        var (inked, partial) = OnDevice(device =>
        {
            var text = new NameText();
            var size = Write(text, device, new NameFormats(), "Downloads", lettering);
            using var bitmap = Drawn(device, text, size);
            var pixels = bitmap.GetPixelBytes();

            var inked = 0;
            var partial = 0;

            for (var alpha = 3; alpha < pixels.Length; alpha += 4)
            {
                inked += pixels[alpha] != 0 ? 1 : 0;
                partial += pixels[alpha] is > 0 and < 255 ? 1 : 0;
            }

            return (inked, partial);
        });

        Assert.True(inked > 0, "Nothing was drawn.");
        Assert.Equal(smoothed, partial > 0);
    }

    /// <summary>
    /// A name in the application's type too long for its room takes only its room, cut short there,
    /// as a text block does, and one that fits takes only its own width.
    /// </summary>
    [Fact]
    public void AStandardNameTooLongForItsRoomTakesOnlyItsRoom()
    {
        var (cut, whole) = OnDevice(device =>
        {
            var formats = new NameFormats();
            var cut = new NameText().Write(device, formats, "A name far too long for its shape", MapLettering.Standard, 40, false, Ink, 13, 1);
            var whole = new NameText().Write(device, formats, "A", MapLettering.Standard, 40, false, Ink, 13, 1);

            return (cut.Width, whole.Width);
        });

        Assert.Equal(40, cut);
        Assert.InRange(whole, 1, 39);
    }

    /// <summary>
    /// Each line of a centred name in SpaceMonger's type is centred in its box, and each line of one
    /// that is not starts at its left. The format is shared by every name, so the alignment is the
    /// name's own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShortLineIsCentredOnlyInACentredName(bool centred)
    {
        var (start, width) = OnDevice(device =>
        {
            var text = new NameText();
            var size = text.Write(device, new NameFormats(), "Downloads\nD", MapLettering.Classic, 200, centred, Ink, 13, 1);
            using var bitmap = Drawn(device, text, size);

            return (FirstInkedColumn(bitmap, fromRow: 13, toRow: 26), (int)bitmap.SizeInPixels.Width);
        });

        Assert.True(start < width, "The second line has no ink.");

        if (centred)
        {
            Assert.True(start > width / 4, $"The second line started at {start} of {width}.");
        }
        else
        {
            Assert.True(start < 3, $"The second line started at {start} of {width}.");
        }
    }

    [Fact]
    public void NamesOfOneSpacingOrTextSizeShareOneFormat()
    {
        var formats = new NameFormats();

        Assert.Same(formats.Classic(13), formats.Classic(13));
        Assert.NotSame(formats.Classic(13), formats.Classic(20));
        Assert.Equal(20, formats.Classic(20).LineSpacing);
        Assert.Same(formats.Standard(1.5), formats.Standard(1.5));
        Assert.Equal(18, formats.Standard(1.5).FontSize);
    }

    private static Windows.Foundation.Size Write(NameText text, CanvasDevice device, NameFormats formats, string written, MapLettering lettering) =>
        text.Write(device, formats, written, lettering, room: 200, centred: true, Ink, spacing: 13, textScale: 1);

    /// <summary>What <paramref name="work"/> returns on the map's device for a compositor of its own.</summary>
    private static T OnDevice<T>(Func<CanvasDevice, T> work) =>
        CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();

            return work(CompositionDevice.For(compositor).Current);
        });

    /// <summary><paramref name="text"/> drawn into a bitmap of its <paramref name="size"/>.</summary>
    private static CanvasRenderTarget Drawn(CanvasDevice device, NameText text, Windows.Foundation.Size size)
    {
        var bitmap = new CanvasRenderTarget(device, (float)Math.Ceiling(size.Width), (float)Math.Ceiling(size.Height), 96);

        using var session = bitmap.CreateDrawingSession();
        session.Clear(Color.FromArgb(0, 0, 0, 0));
        text.Draw(session);

        return bitmap;
    }

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
}
