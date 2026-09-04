using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// Draws a drive one way in the picker's list and another in the box above it.
///
/// <para>A combo box draws the chosen entry with the same template as the list, and the Explore
/// toolbar has no room for the wide one. At the window's own default width, a box carrying the
/// space figures pushes the Colour picker off the end, and one carrying merely the volume label
/// pushes the folder scope's own button off it. The list is where the choice is made, so that is
/// where everything but the mount point belongs.</para>
///
/// <para>The container is the only thing that tells the two apart. WinUI passes the
/// <see cref="ComboBoxItem"/> when it is filling the list, and nothing of the sort when it is
/// filling the box.</para>
/// </summary>
public sealed partial class DriveEntryTemplates : DataTemplateSelector
{
    /// <summary>How the box states the drive that is already chosen.</summary>
    public DataTemplate? Chosen { get; set; }

    /// <summary>How a row of the open list offers a drive.</summary>
    public DataTemplate? Listed { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        container is ComboBoxItem ? Listed : Chosen;

    protected override DataTemplate? SelectTemplateCore(object item) => Chosen;
}
