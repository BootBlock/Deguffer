using System.Windows.Input;
using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// The Storage rows waiting for a source folder, folded into one group below the list. See
/// <see cref="AwaitingFolderGroupViewModel"/>.
/// </summary>
public sealed partial class AwaitingFolderGroup
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(
        nameof(Group),
        typeof(AwaitingFolderGroupViewModel),
        typeof(AwaitingFolderGroup),
        new PropertyMetadata(null));

    public static readonly DependencyProperty AddCommandProperty = DependencyProperty.Register(
        nameof(AddCommand),
        typeof(ICommand),
        typeof(AwaitingFolderGroup),
        new PropertyMetadata(null));

    public AwaitingFolderGroup()
    {
        InitializeComponent();

        // The motion the list above has, so a row arriving here reads the same as one arriving there.
        ListAnimation.Play(RowsList, SystemMotion.Current).Arrived += BarMotion.GrowIn;
        FindingRowNames.Follow(RowsList);
    }

    public AwaitingFolderGroupViewModel? Group
    {
        get => (AwaitingFolderGroupViewModel?)GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    /// <summary>
    /// What the group's button does: asks for a source folder and plans the group's rows again. The
    /// page's command, so the button is off while the page is busy, as Scan is.
    /// </summary>
    public ICommand? AddCommand
    {
        get => (ICommand?)GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    /// <summary>
    /// The row template, which the page owns and sets on the list and the group together, so both
    /// draw at the density the user chose.
    /// </summary>
    public DataTemplate RowTemplate
    {
        get => RowsList.ItemTemplate;
        set => RowsList.ItemTemplate = value;
    }
}
