using Deguffer.Core.Viewing;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// <summary>
    /// The layer over each viewport, shared by every list seen through it: a page that scrolls holds
    /// several repeaters, and each would otherwise stack a layer of its own over the same viewport.
    /// </summary>
    private static readonly ConditionalWeakTable<ScrollContentPresenter, RowGhosts> Layers = [];

    private readonly GhostCanvas _layer;

    private RowGhosts(GhostCanvas layer) => _layer = layer;

    /// <summary>
    /// A layer over the viewport <paramref name="viewport"/> presents, beside it in the scroller's
    /// own template, or null where the template does not lay its viewport out in a panel.
    /// </summary>
    public static RowGhosts? Over(ScrollContentPresenter viewport)
    {
        if (Layers.TryGetValue(viewport, out var shared))
        {
            return shared;
        }

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

        // Fitted now as well as on a resize, because a list found after its scroller was laid out
        // hears no size change until the window is resized.
        Fit();
        viewport.SizeChanged += (_, _) => Fit();

        panel.Children.Insert(panel.Children.IndexOf(viewport) + 1, layer);

        var ghosts = new RowGhosts(new GhostCanvas(layer));
        Layers.Add(viewport, ghosts);

        return ghosts;

        // Inset as the viewport is, which a scroller with padding moves in from the cell they share,
        // so a ghost's place in the viewport is its place on the layer. Clipped to the viewport, so
        // a ghost near an edge is cut where the rows are.
        void Fit()
        {
            layer.Margin = viewport.Margin;
            layer.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight),
            };
        }
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

            return new Stood(ContentOf(container, at), null, list.ItemTemplate, list.ItemTemplateSelector);
        }

        /// <summary>
        /// How a group heading standing at <paramref name="at"/> is drawn: by the template its list's
        /// group style gave it, which is not the list's template for a row.
        /// </summary>
        public static Stood Of(ListViewBaseHeaderItem heading, Windows.Foundation.Rect at) =>
            new(ContentOf(heading, at), null, heading.ContentTemplate, heading.ContentTemplateSelector);

        /// <summary>
        /// How <paramref name="root"/>, a template's root standing at <paramref name="at"/>, is drawn
        /// by <paramref name="template"/>: a <see cref="DataTemplate"/> or a <see cref="DataTemplateSelector"/>.
        /// </summary>
        public static Stood Of(FrameworkElement root, Windows.Foundation.Rect at, object? template) =>
            new(WithMargin(root, at), null, template as DataTemplate, template as DataTemplateSelector);

        /// <summary>Where the content of <paramref name="container"/>, standing at <paramref name="at"/>, stood.</summary>
        private static Windows.Foundation.Rect ContentOf(ContentControl container, Windows.Foundation.Rect at)
        {
            if (container.ContentTemplateRoot is not FrameworkElement root || VisualTreeHelper.GetParent(root) is null)
            {
                return at;
            }

            var origin = root.TransformToVisual(container).TransformPoint(default);

            return WithMargin(root, new Windows.Foundation.Rect(at.X + origin.X, at.Y + origin.Y, root.ActualWidth, root.ActualHeight));
        }

        /// <summary>
        /// Where a template's root stood, with its margin. The ghost draws the template again, margin
        /// and all, so a ghost sized to the root alone would squeeze it by the margin.
        /// </summary>
        private static Windows.Foundation.Rect WithMargin(FrameworkElement root, Windows.Foundation.Rect at)
        {
            var margin = root.Margin;

            return new Windows.Foundation.Rect(
                at.X - margin.Left,
                at.Y - margin.Top,
                Math.Max(0, at.Width + margin.Left + margin.Right),
                Math.Max(0, at.Height + margin.Top + margin.Bottom));
        }
    }

    /// <summary>
    /// Fade each of a change's leaving rows out where it stood, as <paramref name="played"/> plays.
    /// </summary>
    public void Leave(IReadOnlyList<(object Item, Stood Stood)> leaving, Motion played) => Leave(_layer, leaving, played);

    /// <summary>
    /// Add a ghost for each row in <paramref name="leaving"/> to <paramref name="layer"/>, lay the layer
    /// out once for them all, then fade each. A ghost is laid out before it is faded because only then
    /// does it hold the controls to make unreachable and stand where it can be aligned, and the pass is
    /// shared because each one lays out the whole window.
    /// </summary>
    public static void Leave<TGhost>(IGhostLayer<TGhost> layer, IReadOnlyList<(object Item, Stood Stood)> leaving, Motion played)
    {
        if (leaving.Count == 0)
        {
            return;
        }

        var ghosts = new TGhost[leaving.Count];

        for (var at = 0; at < leaving.Count; at++)
        {
            ghosts[at] = layer.Add(leaving[at].Item, leaving[at].Stood);
        }

        layer.LayOut();

        for (var at = 0; at < leaving.Count; at++)
        {
            layer.Fade(ghosts[at], leaving[at].Stood, played);
        }
    }
}
