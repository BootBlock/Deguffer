using Deguffer.Core.Duplicates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Renders a duplicate removal's confirmation as a dialog: its title, its summary, each warning, and
/// every copy that goes (§7.4). Every word comes from the <see cref="RemovalConfirmation"/>; this
/// chooses only how it looks.
/// </summary>
public sealed class ContentDialogDuplicateConfirmation(XamlRoot xamlRoot, ElementTheme theme)
    : IDuplicateConfirmationPrompt
{
    public async Task<bool> AskAsync(RemovalConfirmation confirmation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        // Checked before the dialog is built, on ContentDialogConfirmationPrompt's reasoning: a
        // token already cancelled would otherwise fire Hide against a dialog ShowAsync has not yet
        // opened, leaving a modal up that the cancel path can no longer take down.
        ct.ThrowIfCancellationRequested();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = confirmation.Summary, TextWrapping = TextWrapping.WrapWholeWords });

        foreach (var warning in confirmation.Warnings)
        {
            content.Children.Add(new TextBlock
            {
                Text = warning,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontWeight = FontWeights.SemiBold,
            });
        }

        // Every copy that goes, because a catalogue can name a copy Deguffer cannot see is in use,
        // and the list is where the user can. Virtualised, since a rule can mark thousands.
        var copies = new ListView
        {
            ItemsSource = confirmation.Copies.Select(copy => copy.Path).ToList(),
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 280,
        };
        AutomationProperties.SetName(copies, "Every copy that goes");
        content.Children.Add(copies);

        if (confirmation.Staying.Count > 0)
        {
            var staying = new ListView
            {
                ItemsSource = confirmation.Staying.Select(copy => $"{copy.Copy.Path}\n{copy.Why}").ToList(),
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 200,
            };
            AutomationProperties.SetName(staying, "Every marked copy that stays");
            content.Children.Add(staying);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,

            // The popup layer does not inherit the theme applied to the window root, so without
            // this it renders dark over a light window.
            RequestedTheme = theme,

            Title = confirmation.Title,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = confirmation.ConfirmLabel,
            CloseButtonText = "Cancel",

            // Cancel is the default: a rule may have marked what this lists, and nobody should
            // remove it from one keystroke without reading it.
            DefaultButton = ContentDialogButton.Close,
        };

        using var registration = ct.Register(dialog.Hide);

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
