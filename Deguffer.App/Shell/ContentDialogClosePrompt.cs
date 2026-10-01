using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>Asks, in a dialog, what to do about a close while something is running. The words are Core's.</summary>
internal static class ContentDialogClosePrompt
{
    /// <summary>
    /// True where the user chose to close once the run is over, false where they chose to keep the
    /// window open or dismissed the dialog, and null where another dialog was already on screen and
    /// nothing was asked.
    /// </summary>
    public static async Task<bool?> AskAsync(ClosePrompt prompt, XamlRoot xamlRoot, ElementTheme theme)
    {
        if (ModalDialog.IsShowing)
        {
            return null;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,

            // The popup layer does not inherit the window root's theme.
            RequestedTheme = theme,
            Title = prompt.Title,
            Content = new TextBlock { Text = prompt.Consequence, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = prompt.CloseWhenDoneLabel,
            CloseButtonText = prompt.KeepOpenLabel,

            // Keeping the window open is the default, so one keystroke never commits the user to a
            // window that later disappears on its own.
            DefaultButton = ContentDialogButton.Close,
        };

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
