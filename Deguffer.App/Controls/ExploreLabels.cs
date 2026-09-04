using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The names laid over the map, as real text rather than as pixels in the bitmap.
///
/// <para>Text controls, so they scale with the user's text size and a screen reader could reach
/// them — neither of which a name burnt into a bitmap offers. There are only ever a few dozen,
/// because a shape too small to read is a shape the drawing gives no label.</para>
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about putting a few
/// dozen pieces of text where somebody else said they go (G1).</para>
/// </summary>
internal sealed class ExploreLabels : Canvas
{
    public ExploreLabels() =>

        // Never the thing being clicked. A click that landed on a name rather than the shape under
        // it would select whatever that name happened to overlap.
        IsHitTestVisible = false;

    /// <summary>
    /// Put a name on each shape <paramref name="drawing"/> chose to label, at
    /// <paramref name="scale"/> bitmap pixels to the device-independent pixel.
    ///
    /// <para>The text blocks are kept and written over rather than rebuilt. A scan repaints this
    /// several times a second, and each rebuild would throw away a few dozen controls and their
    /// brushes and make the framework measure and arrange a fresh set of them, for text that has
    /// usually not changed (G5).</para>
    /// </summary>
    public void Show(ExploreTree tree, ExploreSurface drawing, double scale)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(drawing);

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

            text.Text = $"{tree.NameOf(label.Node)}  {FreeSpace.Format(tree.SizeOf(label.Node))}";
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
    /// <para>For while a resize settles. The bitmap stretches with the control and these do not,
    /// because they are controls at fixed positions rather than part of the picture — so left up
    /// they would sit over whichever shape had moved under them, naming it wrongly.</para>
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
