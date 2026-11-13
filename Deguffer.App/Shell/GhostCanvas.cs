using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Deguffer.App.Shell;

/// <summary>
/// The canvas over a viewport that ghosts are drawn on: each ghost a fresh container drawn from the
/// list's own template and the item that has gone.
/// </summary>
internal sealed class GhostCanvas(Canvas layer) : IGhostLayer<ContentControl>
{
    public ContentControl Add(object item, RowGhosts.Stood stood)
    {
        var ghost = new ContentControl
        {
            Content = item,
            ContentTemplate = stood.Template,
            ContentTemplateSelector = stood.Selector,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Width = stood.At.Width,
            Height = stood.At.Height,
            IsTabStop = false,
        };
        Canvas.SetLeft(ghost, stood.At.X);
        Canvas.SetTop(ghost, stood.At.Y);
        layer.Children.Add(ghost);

        return ghost;
    }

    // One pass lays out every ghost waiting on the layer, and the rest of the window with them.
    public void LayOut() => layer.UpdateLayout();

    public void Fade(ContentControl ghost, RowGhosts.Stood stood, Motion played)
    {
        Inert(ghost);
        Align(ghost, stood);

        var visual = ElementCompositionPreview.GetElementVisual(ghost);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = played.Duration;

        var fading = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        fading.End();
        fading.Completed += (_, _) => layer.Children.Remove(ghost);
    }

    /// <summary>
    /// Make every part of a ghost unreachable. It is drawn from the row's own template, so it holds the
    /// row's controls: a checkbox or a Keep button that could take focus from the keyboard would act on
    /// an item that has left the list, such as one a search has just hidden. The layer already takes no
    /// pointer input, and hiding the layer from assistive technology does not hide what it holds, so
    /// each part is taken out of the tab order and out of what a screen reader reads.
    /// </summary>
    private static void Inert(DependencyObject root)
    {
        foreach (var part in VisualTree.Descendants<DependencyObject>(root).Prepend(root))
        {
            if (part is UIElement element)
            {
                AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
            }

            if (part is Control control)
            {
                control.IsTabStop = false;
            }
        }
    }

    /// <summary>Move a tree row's ghost until its content stands where the row's content did.</summary>
    private void Align(ContentControl ghost, RowGhosts.Stood stood)
    {
        if (stood.Branch is not { } wanted)
        {
            return;
        }

        if (ghost.ContentTemplateRoot is TreeViewItem { Content: FrameworkElement content })
        {
            var drawn = content.TransformToVisual(layer).TransformPoint(default);
            // Moved by the difference and narrowed by it, so the row's right edge stays where it was.
            var shift = wanted.X - drawn.X;
            Canvas.SetLeft(ghost, stood.At.X + shift);
            ghost.Width = Math.Max(0, stood.At.Width - shift);
        }
    }
}
