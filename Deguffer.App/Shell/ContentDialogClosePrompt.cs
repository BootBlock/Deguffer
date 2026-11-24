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
    /// <param name="owner">The window's root element, which the dialog opens over.</param>
    public static async Task<bool?> AskAsync(ClosePrompt prompt, FrameworkElement owner)
    {
        if (ModalDialog.IsShowing)
        {
            return null;
        }

        // Keeping the window open is the default button, so one keystroke never commits the user to
        // a window that later disappears on its own.
        var dialog = DialogFactory.Question(
            owner,
            prompt.Title,
            DialogFactory.Paragraph(prompt.Consequence),
            prompt.CloseWhenDoneLabel,
            prompt.KeepOpenLabel);

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
