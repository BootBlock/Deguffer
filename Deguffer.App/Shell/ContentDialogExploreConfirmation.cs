using Deguffer.Core.Exploring.Acting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Renders an Explore removal as a dialog. Every word comes from the prompt; this chooses only how
/// it looks.
/// </summary>
public sealed class ContentDialogExploreConfirmation(FrameworkElement owner)
    : IExploreConfirmationPrompt
{
    public async Task<bool> AskAsync(ExploreRemovalPrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // Cancel is the default button for both routes, not only the permanent one. A user who
        // reached this dialog by pressing Delete on a highlighted row is one keystroke from
        // confirming something they have not read.
        var dialog = DialogFactory.Question(owner, prompt.Title, DialogFactory.Paragraph(prompt.Consequence), prompt.ConfirmLabel);

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary;
    }
}
