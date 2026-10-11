using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Shell;

/// <summary>
/// The rows a bound list lays out, as <see cref="ListAnimation"/> reads them: which container shows
/// which row, what changed in the list's items, and how a row is drawn. A list view and a repeater
/// answer each of these differently, and the motion is the same for both.
/// </summary>
internal interface IListRows
{
    /// <summary>The control the rows are laid out in.</summary>
    FrameworkElement List { get; }

    /// <summary>Raised as a container is given a row to show, with the container and the row.</summary>
    event Action<UIElement, object>? Shown;

    /// <summary>Raised as a container lets go of its row, to be shown again for another.</summary>
    event Action<UIElement>? Released;

    /// <summary>
    /// Raised for each change to the list's items, before the list lays itself out again, with the
    /// rows the change put into the list and whether it was a reset, after which nothing says which
    /// rows are new.
    /// </summary>
    event Action<IReadOnlyList<object>, bool>? Changed;

    /// <summary>
    /// The scroller the rows are seen through, or null before the list is in the tree, or where no
    /// scroller is around it. Such a list does not animate.
    /// </summary>
    ScrollViewer? Scroller();

    /// <summary>Every row the list holds, on screen or not.</summary>
    HashSet<object> Held();

    /// <summary>Every container laid out now with a row, and its row.</summary>
    IEnumerable<(UIElement Container, object Row)> Realised();

    /// <summary>
    /// How <paramref name="container"/>, standing at <paramref name="at"/> in <paramref name="viewport"/>,
    /// is drawn: the row a ghost of it would show.
    /// </summary>
    RowGhosts.Stood Draw(UIElement container, Rect at, UIElement viewport);
}
