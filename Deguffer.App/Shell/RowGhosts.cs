using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>
/// The rows a list has just let go of, drawn where they stood as they fade out.
///
/// <para>A list keeps the container of a row that has gone, empties it at once and parks it out of
/// sight, so neither the container nor an implicit hide animation on it can show the row leaving.
/// A ghost is a fresh container drawn from the list's own template and the item that has gone, in a
/// layer over the list's viewport that takes no input and is hidden from assistive technology: the
/// row has already gone from the list a screen reader reads.</para>
/// </summary>
internal sealed class RowGhosts
{
    private readonly Canvas _layer;

    private RowGhosts(Canvas layer) => _layer = layer;

    /// <summary>
    /// A layer over the viewport <paramref name="viewport"/> presents, beside it in the scroller's
    /// own template, or null where the template does not lay its viewport out in a panel.
    /// </summary>
    public static RowGhosts? Over(ScrollContentPresenter viewport)
    {
        if (VisualTreeHelper.GetParent(viewport) is not Panel panel)
        {
            return null;
        }

        var layer = new Canvas { IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(layer, AccessibilityView.Raw);

        if (panel is Grid)
        {
            Grid.SetRow(layer, Grid.GetRow(viewport));
            Grid.SetRowSpan(layer, Grid.GetRowSpan(viewport));
            Grid.SetColumn(layer, Grid.GetColumn(viewport));
            Grid.SetColumnSpan(layer, Grid.GetColumnSpan(viewport));
        }

        // Clipped to the viewport, so a ghost near an edge is cut where the rows are.
        viewport.SizeChanged += (_, _) => layer.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight),
        };

        panel.Children.Insert(panel.Children.IndexOf(viewport) + 1, layer);

        return new RowGhosts(layer);
    }

    /// <summary>
    /// How a row stood on screen, read before the list changed: where its content was, and the
    /// template that drew it. The template is read then, because a list shown as something else
    /// changes its template with its items, and a row drawn with the template for another kind of
    /// item would not bind. Where the content was rather than where the container was, because the
    /// container's own chrome, such as a checkbox for picking several rows, is not part of the ghost.
    /// </summary>
    /// <param name="Branch">
    /// For a tree's row, where the row's own content was. A tree's template is its row, container and
    /// all, and drawn alone it lays out its depth and its expander differently, so its ghost is moved
    /// until its content stands there.
    /// </param>
    public readonly record struct Stood(
        Windows.Foundation.Rect At,
        Windows.Foundation.Point? Branch,
        DataTemplate? Template,
        DataTemplateSelector? Selector)
    {
        /// <summary>
        /// How <paramref name="container"/>, standing at <paramref name="at"/> in <paramref name="viewport"/>,
        /// is drawn: the row a ghost of it would show.
        /// </summary>
        public static Stood Of(ItemsControl list, SelectorItem container, Windows.Foundation.Rect at, UIElement viewport)
        {
            // A tree's template makes the container itself, so its ghost stands where the container did.
            if (container is TreeViewItem branch)
            {
                var branchContent = branch.Content is FrameworkElement inside && VisualTreeHelper.GetParent(inside) is not null
                    ? inside.TransformToVisual(viewport).TransformPoint(default)
                    : (Windows.Foundation.Point?)null;

                return new Stood(at, branchContent, list.ItemTemplate, list.ItemTemplateSelector);
            }

            var content = at;

            if (container.ContentTemplateRoot is FrameworkElement root && VisualTreeHelper.GetParent(root) is not null)
            {
                var origin = root.TransformToVisual(container).TransformPoint(default);
                content = new Windows.Foundation.Rect(at.X + origin.X, at.Y + origin.Y, root.ActualWidth, root.ActualHeight);
            }

            return new Stood(content, null, list.ItemTemplate, list.ItemTemplateSelector);
        }
    }

    /// <summary>Fade <paramref name="item"/> out where its row stood, as <paramref name="played"/> plays.</summary>
    public void Leave(object item, Stood stood, Motion played)
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
        _layer.Children.Add(ghost);
        ghost.UpdateLayout();
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
        fading.Completed += (_, _) => _layer.Children.Remove(ghost);
    }

    /// <summary>
    /// Make every part of a ghost unreachable. It is drawn from the row's own template, so it holds the
    /// row's controls: a checkbox or a Keep button that could take focus from the keyboard would act on
    /// an item that has left the list, such as one a search has just hidden. The layer already takes no
    /// pointer input, and hiding the layer from assistive technology does not hide what it holds, so
    /// each part is taken out of the tab order and out of what a screen reader reads.
    /// </summary>
    private static void Inert(DependencyObject part)
    {
        if (part is UIElement element)
        {
            AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        }

        if (part is Control control)
        {
            control.IsTabStop = false;
        }

        for (var at = 0; at < VisualTreeHelper.GetChildrenCount(part); at++)
        {
            Inert(VisualTreeHelper.GetChild(part, at));
        }
    }

    /// <summary>Move a tree row's ghost until its content stands where the row's content did.</summary>
    private void Align(ContentControl ghost, Stood stood)
    {
        if (stood.Branch is not { } wanted)
        {
            return;
        }

        if (ghost.ContentTemplateRoot is TreeViewItem { Content: FrameworkElement content })
        {
            var drawn = content.TransformToVisual(_layer).TransformPoint(default);
            // Moved by the difference and narrowed by it, so the row's right edge stays where it was.
            var shift = wanted.X - drawn.X;
            Canvas.SetLeft(ghost, stood.At.X + shift);
            ghost.Width = Math.Max(0, stood.At.Width - shift);
        }
    }
}
