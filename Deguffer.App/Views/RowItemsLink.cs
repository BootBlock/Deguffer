using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The link that lists a Storage row's items, found in the row's container so the keyboard can go back
/// to it when the item list closes.
/// </summary>
internal static class RowItemsLink
{
    /// <summary>Give the keyboard to <paramref name="row"/>'s items link in <paramref name="list"/>, where the row is still shown.</summary>
    public static void Focus(ListViewBase list, FindingViewModel row)
    {
        if (list.ContainerFromItem(row) is DependencyObject container && In(container, row) is { } link)
        {
            link.Focus(FocusState.Programmatic);
        }
    }

    private static HyperlinkButton? In(DependencyObject parent, FindingViewModel row) =>
        VisualTree.Descendants<HyperlinkButton>(parent).FirstOrDefault(link =>
            link.Tag is FindingViewModel tagged
            && ReferenceEquals(tagged, row)
            && AutomationProperties.GetName(link) == row.Text.ItemsLinkName);
}
