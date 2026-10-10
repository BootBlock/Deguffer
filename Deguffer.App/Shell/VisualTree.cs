using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>What the framework has drawn under an element, for the code that has to reach into a template it did not build.</summary>
internal static class VisualTree
{
    /// <summary>
    /// Every element of type <typeparamref name="T"/> under <paramref name="root"/>, not counting the
    /// root itself, each before what lies under it and in the order the framework holds them. Lazy, so
    /// a search for the first one stops there.
    /// </summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var at = 0; at < VisualTreeHelper.GetChildrenCount(root); at++)
        {
            var child = VisualTreeHelper.GetChild(root, at);

            if (child is T found)
            {
                yield return found;
            }

            foreach (var below in Descendants<T>(child))
            {
                yield return below;
            }
        }
    }
}
