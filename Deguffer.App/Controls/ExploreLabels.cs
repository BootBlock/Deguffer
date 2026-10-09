using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The names laid over the map, as real text rather than as pixels in the picture.
///
/// <para>Text controls, so they scale with the user's text size and a screen reader could reach
/// them — neither of which a name burnt into the picture offers. There are only ever a few dozen,
/// because a shape too small to read is a shape the drawing gives no label.</para>
///
/// <para>Placed in the picture the way the drawing they name is, and moved by the map's camera, as the
/// drawings and the outlines are. They are still taken off while the picture moves, because text
/// magnified with a picture is not text at the reader's size.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting a few
/// dozen pieces of text where somebody else said they go (G1).</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    /// <summary>
    /// Where the drawing the names were laid out for lies in the picture, per device-independent
    /// pixel of that drawing: a <c>Scale</c> and an <c>Offset</c>, combined with the camera's by the
    /// compositor.
    /// </summary>
    private readonly CompositionPropertySet _placed;

    /// <summary>
    /// Names that move with <paramref name="camera"/>, wherever <see cref="Place"/> puts them in the
    /// picture. Through this element's own visual, which XAML lays out at the map's corner and
    /// leaves the scale and the translation of to whoever sets them.
    /// </summary>
    public ExploreLabels(MapCamera camera)
    {
        // Never the thing being clicked. A click that landed on a name rather than the shape under
        // it would select whatever that name happened to overlap.
        IsHitTestVisible = false;

        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        _placed = compositor.CreatePropertySet();
        _placed.InsertVector3(nameof(Visual.Scale), Vector3.One);
        _placed.InsertVector3(nameof(Visual.Offset), Vector3.Zero);

        var scale = compositor.CreateExpressionAnimation(
            "Vector3(camera.Scale.X * placed.Scale.X, camera.Scale.Y * placed.Scale.Y, 1)");
        var translation = compositor.CreateExpressionAnimation(
            "Vector3((camera.Scale.X * placed.Offset.X) + camera.Offset.X, (camera.Scale.Y * placed.Offset.Y) + camera.Offset.Y, 0)");

        foreach (var expression in new[] { scale, translation })
        {
            expression.SetReferenceParameter("camera", camera.Properties);
            expression.SetReferenceParameter("placed", _placed);
        }

        ElementCompositionPreview.SetIsTranslationEnabled(this, true);
        visual.StartAnimation(nameof(Visual.Scale), scale);
        visual.StartAnimation("Translation", translation);
    }

    /// <summary>
    /// Put the names where <paramref name="placed"/> says one device-independent pixel of the drawing
    /// they name lies in the picture.
    /// </summary>
    public void Place(MapTransform placed)
    {
        _placed.InsertVector3(nameof(Visual.Scale), new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1));
        _placed.InsertVector3(nameof(Visual.Offset), new Vector3((float)placed.X, (float)placed.Y, 0));
    }

    /// <summary>
    /// Put <paramref name="caption"/>'s text on each shape <paramref name="drawing"/> chose to label,
    /// at <paramref name="scale"/> canvas pixels to the device-independent pixel.
    ///
    /// <para>The caption comes from the page rather than from here, because only it knows what the
    /// tree's nodes are: a drive's are a file name and a size, and a memory picture's are a process or
    /// a part of Windows.</para>
    ///
    /// <para>The text blocks are kept and written over rather than rebuilt. A scan repaints this
    /// several times a second, and each rebuild would throw away a few dozen controls and their
    /// brushes and make the framework measure and arrange a fresh set of them, for text that has
    /// usually not changed (G5).</para>
    /// </summary>
    public void Show(ExploreSurface drawing, double scale, Func<ExploreLabel, string> caption)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(caption);

        Visibility = Visibility.Visible;

        while (Children.Count < drawing.Labels.Count)
        {
            Children.Add(NewLabel());
        }

        while (Children.Count > drawing.Labels.Count)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        for (var i = 0; i < drawing.Labels.Count; i++)
        {
            var label = drawing.Labels[i];
            var text = (TextBlock)Children[i];

            text.Text = caption(label);
            text.TextAlignment = label.Centred ? TextAlignment.Center : TextAlignment.Left;
            text.Width = label.Width / scale;

            ((SolidColorBrush)text.Foreground).Color = Color.FromArgb(
                255, label.Colour.Red, label.Colour.Green, label.Colour.Blue);

            ((RotateTransform)text.RenderTransform).Angle = label.Rotation;

            SetLeft(text, label.X / scale);
            SetTop(text, label.Y / scale);
        }
    }

    /// <summary>
    /// Take the names off until the layout that places them arrives.
    ///
    /// <para>For while a resize settles or the picture moves. The names go with the picture, but
    /// stretched or magnified with it they are text out of shape and away from the reader's size,
    /// and a name the layout gave room to at one size can run over its neighbours at another.</para>
    /// </summary>
    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>
    /// Put them back without redrawing them, for a caller that has dropped the redraw which would
    /// have. The positions are still the ones the last drawing gave, because a size that was never
    /// drawn never moved them.
    /// </summary>
    public void Reveal() => Visibility = Visibility.Visible;

    /// <summary>Take them off for good, for a map that is no longer showing anything.</summary>
    public void Clear()
    {
        Children.Clear();
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// One reusable piece of label text, with everything a repaint never changes already set —
    /// including the brush and the transform, which are written through rather than replaced.
    /// </summary>
    private static TextBlock NewLabel()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(),

            // Zero for anything in rectangles, and per label for a sunburst, which turns each one to
            // lie along its own ring. Always present rather than attached only where it turns: a
            // transform of no degrees costs nothing to keep, and a branch here would mean a label
            // reused from a sunburst kept its angle on a treemap.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };

        // Announced through the list view instead: a label here duplicates a row there, and a
        // screen reader reading fifty fragments of a picture helps nobody.
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);

        return text;
    }
}
