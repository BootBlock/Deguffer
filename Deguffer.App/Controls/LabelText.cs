using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The text of one name on the map, set in the drawing's lettering (<see cref="MapLettering"/>): the
/// application's own type as a text block, or SpaceMonger's as aliased text drawn into an image.
///
/// <para>An image for SpaceMonger's because its type is drawn without smoothing, pixel for pixel, and
/// a text block cannot be asked to draw without it. Win2D can, through the drawing session it draws
/// the text with. The image is drawn again only when the text, its size or its colour changes.</para>
/// </summary>
internal sealed class LabelText
{
    /// <summary>SpaceMonger's type: its own bitmap font is one DirectWrite cannot draw, and this is the outline font Windows ships in its place.</summary>
    private const string ClassicFont = "Microsoft Sans Serif";

    /// <summary>SpaceMonger's 8 point type, in device-independent pixels.</summary>
    private const float ClassicSize = 11;

    /// <summary>How far down a line of the classic type its baseline is, as a fraction of the line's height.</summary>
    private const float ClassicBaseline = 0.8f;

    private readonly TextBlock _block;

    private readonly Image _image;

    private CanvasImageSource? _source;

    /// <summary>What the image was last drawn with, so it is drawn again only when that changes, or when its contents are lost.</summary>
    private (string Text, float Width, float Height, float Spacing, bool Centred, Color Colour, double Scale)? _drawn;

    public LabelText()
    {
        _block = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(),
        };

        _image = new Image { Stretch = Stretch.None, Visibility = Visibility.Collapsed };

        Element = new Grid
        {
            Children = { _block, _image },

            // Zero for anything in rectangles, and per label for a sunburst, which turns each one to
            // lie along its own ring. Always present rather than attached only where it turns: a
            // transform of no degrees costs nothing to keep, and a branch here would mean a label
            // reused from a sunburst kept its angle on a treemap.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };

        // Announced through the list view instead: a label here duplicates a row there, and a screen
        // reader reading fifty fragments of a picture helps nobody.
        AutomationProperties.SetAccessibilityView(Element, AccessibilityView.Raw);
    }

    /// <summary>The element the name is on the map as.</summary>
    public FrameworkElement Element { get; }

    /// <summary>
    /// Write <paramref name="text"/> in <paramref name="lettering"/>, in <paramref name="colour"/>,
    /// turned <paramref name="rotation"/> degrees, in a box <paramref name="room"/> device-independent
    /// pixels wide: centred in it where <paramref name="centred"/> and it fits, and otherwise from its
    /// left, cut short at its right. Lines of SpaceMonger's type are <paramref name="lineHeight"/>
    /// apart.
    /// </summary>
    /// <returns>How much room the text takes, in device-independent pixels.</returns>
    public Size Write(string text, MapLettering lettering, double room, bool centred, Color colour, double rotation, double lineHeight, double scale)
    {
        ((RotateTransform)Element.RenderTransform).Angle = rotation;

        var classic = lettering == MapLettering.Classic;

        _block.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        _image.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;

        return classic
            ? Draw(text, (float)room, centred, colour, (float)lineHeight, scale)
            : Set(text, room, colour);
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

    private Size Set(string text, double room, Color colour)
    {
        _block.Text = text;
        _block.MaxWidth = room;
        ((SolidColorBrush)_block.Foreground).Color = colour;

        // Measured here rather than left to the layout pass, because where the text goes and when it
        // fades both depend on how much of the box it fills. The pass that follows asks the same
        // question and is answered from this.
        _block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        return _block.DesiredSize;
    }

    private Size Draw(string text, float room, bool centred, Color colour, float spacing, double scale)
    {
        var device = CanvasDevice.GetSharedDevice();
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

        using var format = new CanvasTextFormat
        {
            FontFamily = ClassicFont,
            FontSize = ClassicSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
            LineSpacingMode = CanvasLineSpacingMode.Uniform,
            LineSpacing = spacing,
            LineSpacingBaseline = spacing * ClassicBaseline,
        };

        float natural;

        using (var measuring = new CanvasTextLayout(device, text, format, float.MaxValue, float.MaxValue))
        {
            natural = (float)Math.Ceiling(measuring.LayoutBounds.Width);
        }

        // Centred in its box where it fits; where it does not, from the box's left, cut off at its
        // right, as SpaceMonger writes a name too long for its box.
        var fits = natural <= room;
        var width = Math.Max(1, fits ? natural : room);
        var height = Math.Max(1, lines * spacing);

        format.HorizontalAlignment = centred && fits ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left;

        var wanted = (text, width, height, spacing, centred, colour, scale);

        if (_drawn != wanted)
        {
            if (_source is null || _source.Size.Width != width || _source.Size.Height != height || _source.Dpi != 96 * (float)scale)
            {
                _source = new CanvasImageSource(device, width, height, 96 * (float)scale);
                _image.Source = _source;
            }

            using var layout = new CanvasTextLayout(device, text, format, width, height);
            using var session = _source.CreateDrawingSession(Microsoft.UI.Colors.Transparent);

            session.TextAntialiasing = CanvasTextAntialiasing.Aliased;
            session.DrawTextLayout(layout, 0, 0, colour);

            _drawn = wanted;
        }

        _image.Width = width;
        _image.Height = height;

        return new Size(width, height);
    }
}
