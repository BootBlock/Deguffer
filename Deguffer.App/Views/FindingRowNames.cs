using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// Names each Storage row's container after its row. The container is what assistive technology
/// lands on, and unnamed it is read out as the type of the object behind it.
/// </summary>
internal static class FindingRowNames
{
    public static void Follow(ListViewBase list) => list.ContainerContentChanging += Name;

    private static void Name(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is FindingViewModel row)
        {
            AutomationProperties.SetName(args.ItemContainer, row.Name);
        }
    }
}
