using Deguffer.Core.Exploring.Acting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Puts an Explore removal to the user. What is asked and what they are told comes from
/// <see cref="ExploreRemovalPrompt"/>; this seam only carries it to a surface that can ask.
///
/// Separate from <see cref="IConfirmationPrompt"/> rather than a second method on it, because the
/// two ask different questions of different things. That one renders a §7 requirement about a named
/// provider's plan and may demand a typed phrase; this one asks about a file the user picked out of
/// a picture, which no provider has classified and no tier applies to.
/// </summary>
public interface IExploreConfirmationPrompt
{
    /// <summary>Whether the user said yes. Declining is a decision, not a failure.</summary>
    Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default);
}

/// <summary>
/// Renders an Explore removal as a dialog. Every word comes from the prompt; this chooses only how
/// it looks.
/// </summary>
public sealed class ContentDialogExploreConfirmation(XamlRoot xamlRoot, ElementTheme theme)
    : IExploreConfirmationPrompt
{
    public async Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default)
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

            // Cancel is the default for both routes, not only the permanent one. A user who reached
            // this dialog by pressing Delete on a highlighted row is one keystroke from confirming
            // something they have not read.
            DefaultButton = ContentDialogButton.Close,
        };

        using var registration = ct.Register(dialog.Hide);

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
