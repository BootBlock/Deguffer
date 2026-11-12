using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// One name in SpaceMonger's lettering (<see cref="MapLettering.Classic"/>): its type drawn without
/// smoothing, pixel for pixel, into an image made on the map's own device.
///
/// <para>The map's device (<see cref="CompositionDevice"/>), never Win2D's shared one. A second device
/// brings its own driver threads and GPU memory for the life of the process, and a loss of it is not
/// one the map replaces.</para>
///
/// <para>The image is drawn again only when the text, its size or its colour changes, or its contents
/// are lost. It is made again when the map's device is replaced, because an image made on the old
/// device shows nothing of a write on the new one, and keeps the old device alive. The text is
/// measured again only when it or its spacing changes, which is what its natural width depends on: a
/// name written again unchanged, as most are from one drawing to the next, costs a comparison.</para>
/// </summary>
/// <typeparam name="TImage">What the name is drawn into: see <see cref="ILabelCanvas{TImage}"/>.</typeparam>
internal sealed class ClassicName<TImage>(CompositionDevice device, ClassicFormats formats, ILabelCanvas<TImage> canvas)
    where TImage : class, ICanvasResourceCreatorWithDpi
{
    /// <summary>The text last measured, at its spacing, and how wide it is, in device-independent pixels, with room to spare.</summary>
    private (string Text, float Spacing, float Natural)? _measured;

    /// <summary>What the image was last drawn with, so it is drawn again only when that changes, or when its contents are lost.</summary>
    private (string Text, float Width, float Height, float Spacing, bool Centred, Color Colour, double Scale)? _drawn;

    /// <summary>The size <see cref="Image"/> was made at, which a redraw at the same size keeps it for.</summary>
    private (float Width, float Height, double Scale) _made;

    /// <summary>The image the name is drawn into, or null before it is first drawn.</summary>
    public TImage? Image { get; private set; }

    /// <summary>
    /// Draw <paramref name="text"/> in <paramref name="colour"/>, in a box <paramref name="room"/>
    /// device-independent pixels wide: centred in it where <paramref name="centred"/> and it fits, and
    /// otherwise from its left, cut short at its right. Lines are <paramref name="spacing"/> apart, at
    /// <paramref name="scale"/> device pixels to the device-independent pixel.
    /// </summary>
    /// <returns>
    /// How much room the text takes, in device-independent pixels, or nothing where the device was lost
    /// on the way. The map draws every name again on the device that replaces it.
    /// </returns>
    public Size Draw(string text, float room, bool centred, Color colour, float spacing, double scale)
    {
        var size = default(Size);

        device.UnlessLost(current => size = Draw(current, text, room, centred, colour, spacing, scale));

        return size;
    }

    /// <summary>Draw the image again from what it was last drawn with, for a device that lost its contents.</summary>
    public void Redraw()
    {
        if (_drawn is { } drawn)
        {
            _drawn = null;
            Draw(drawn.Text, drawn.Width, drawn.Centred, drawn.Colour, drawn.Spacing, drawn.Scale);
        }
    }

    private Size Draw(CanvasDevice current, string text, float room, bool centred, Color colour, float spacing, double scale)
    {
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        var format = formats.For(spacing);
        var natural = Measure(current, text, format, spacing);

        // Centred in its box where it fits; where it does not, from the box's left, cut off at its
        // right, as SpaceMonger writes a name too long for its box.
        var fits = natural <= room;
        var width = Math.Max(1, fits ? natural : room);
        var height = Math.Max(1, lines * spacing);

        var wanted = (text, width, height, spacing, centred, colour, scale);

        if (Image is not null && Image.Device != current)
        {
            Image = null;
            _drawn = null;
        }

        if (_drawn != wanted)
        {
            if (Image is null || _made != (width, height, scale))
            {
                Image = canvas.Make(current, width, height, 96 * (float)scale);
                _made = (width, height, scale);
            }

            using var layout = new CanvasTextLayout(current, text, format, width, height)
            {
                HorizontalAlignment = centred && fits ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
            };
            using var session = canvas.Open(Image);

            session.TextAntialiasing = CanvasTextAntialiasing.Aliased;
            session.DrawTextLayout(layout, 0, 0, colour);

            _drawn = wanted;
        }

        return new Size(width, height);
    }

    /// <summary>How wide <paramref name="text"/> is in <paramref name="format"/> with no box to fit, as it was last measured where neither has changed.</summary>
    private float Measure(CanvasDevice current, string text, CanvasTextFormat format, float spacing)
    {
        if (_measured is { } measured && measured.Spacing == spacing && string.Equals(measured.Text, text, StringComparison.Ordinal))
        {
            return measured.Natural;
        }

        using var measuring = new CanvasTextLayout(current, text, format, float.MaxValue, float.MaxValue);
        var natural = (float)Math.Ceiling(measuring.LayoutBounds.Width);

        _measured = (text, spacing, natural);

        return natural;
    }
}
