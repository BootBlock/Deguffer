using Deguffer.Core.Memory.Acting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Puts a Memory close to the user. What is asked and what they are told comes from
/// <see cref="MemoryClosePrompt"/>; this seam only carries it to a surface that can ask.
///
/// Separate from <see cref="IExploreConfirmationPrompt"/> rather than a second method on it, for the
/// reason that one is separate from <see cref="IConfirmationPrompt"/>: the three ask about different
/// subjects. This one asks about another program's unsaved work, which no tier applies to and no
/// preference may switch off (§7.2.1).
/// </summary>
public interface IMemoryConfirmationPrompt
{
    /// <summary>Whether the user said yes. Declining is a decision, not a failure.</summary>
    Task<bool> AskAsync(MemoryClosePrompt prompt, CancellationToken ct = default);
}

/// <summary>
/// Renders a Memory close as a dialog. Every word comes from the prompt; this chooses only how it
/// looks.
/// </summary>
public sealed class ContentDialogMemoryConfirmation(XamlRoot xamlRoot, ElementTheme theme)
    : IMemoryConfirmationPrompt
{
    public async Task<bool> AskAsync(MemoryClosePrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // Checked before the dialog is built, on ContentDialogConfirmationPrompt's reasoning: a
        // token already cancelled would otherwise fire Hide against a dialog ShowAsync has not yet
        // opened, leaving a modal up that the cancel path can no longer take down.
        ct.ThrowIfCancellationRequested();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,

            // The popup layer does not inherit the theme applied to the window root, so without
            // this it renders dark over a light window.
            RequestedTheme = theme,

            Title = prompt.Title,
            Content = new TextBlock
            {
                Text = prompt.Consequence,
                TextWrapping = TextWrapping.WrapWholeWords,
            },
            PrimaryButtonText = prompt.ConfirmLabel,
            CloseButtonText = "Cancel",

            // Cancel is the default. A posted message cannot be recalled and what is at risk is
            // another program's unsaved state, so nobody reaches this from one keystroke.
            DefaultButton = ContentDialogButton.Close,
        };

        using var registration = ct.Register(dialog.Hide);

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
