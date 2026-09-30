using Deguffer.Core.InstalledApps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>Asks an Installed apps question (§7.3) in a dialog. The words are Core's.</summary>
public sealed class ContentDialogInstalledAppsConfirmation(XamlRoot xamlRoot, ElementTheme theme) : IInstalledAppsConfirmation
{
    public async Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct)
    {
        // Before the dialog exists, for ContentDialogConfirmationPrompt's reason: a cancelled token
        // would fire Hide at a dialog ShowAsync has not opened yet, leaving a modal nothing closes.
        ct.ThrowIfCancellationRequested();

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = prompt.Consequence, TextWrapping = TextWrapping.WrapWholeWords });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 240,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, prompt.Items),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,

            // The popup layer does not inherit the window root's theme.
            RequestedTheme = theme,
            Title = prompt.Title,
            Content = content,
            PrimaryButtonText = prompt.ConfirmLabel,
            CloseButtonText = "Cancel",

            // Cancel is the default, so nothing here is confirmed by one keystroke.
            DefaultButton = ContentDialogButton.Close,
        };

        using var registration = ct.Register(dialog.Hide);

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
