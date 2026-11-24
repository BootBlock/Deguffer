using Deguffer.Core.Memory.Acting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Puts a Memory close to the user. What is asked and what they are told comes from
/// <see cref="MemoryClosePrompt"/>; this seam only carries it to a surface that can ask.
///
/// Separate from <see cref="Deguffer.Core.Exploring.Acting.IExploreConfirmationPrompt"/> rather than a second method on it, for the
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
public sealed class ContentDialogMemoryConfirmation(FrameworkElement owner)
    : IMemoryConfirmationPrompt
{
    public async Task<bool> AskAsync(MemoryClosePrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // Cancel is the default button. A posted message cannot be recalled and what is at risk is
        // another program's unsaved state, so nobody reaches this from one keystroke.
        var dialog = DialogFactory.Question(owner, prompt.Title, DialogFactory.Paragraph(prompt.Consequence), prompt.ConfirmLabel);

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary;
    }
}
