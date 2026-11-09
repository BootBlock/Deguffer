using Deguffer.Core.Configuration;
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
/// the text with: see <see cref="ClassicName{TImage}"/>.</para>
/// </summary>
internal sealed class LabelText
{
    private readonly TextBlock _block;

    private readonly Image _image;

    private readonly ClassicName<CanvasImageSource> _classic;

    /// <summary>A name whose SpaceMonger lettering is drawn through <paramref name="device"/>, the map's own.</summary>
    public LabelText(CompositionDevice device)
    {
        _classic = new ClassicName<CanvasImageSource>(device, ImageSourceCanvas.Instance);

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
        _classic.Redraw();
        Show();
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
        var size = _classic.Draw(text, room, centred, colour, spacing, scale);

        Show();
        _image.Width = size.Width;
        _image.Height = size.Height;

        return size;
    }

    /// <summary>Show the image the name is drawn into now, which a replaced device makes anew.</summary>
    private void Show()
    {
        if (!ReferenceEquals(_image.Source, _classic.Image))
        {
            _image.Source = _classic.Image;
        }
    }
}
