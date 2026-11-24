using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Renders a §7 requirement as a dialog. Every judgement here — whether to ask, what to say, what
/// counts as an answer — comes from the <see cref="ConfirmationRequirement"/>; this type chooses
/// only how it looks.
/// </summary>
public sealed class ContentDialogConfirmationPrompt(FrameworkElement owner) : IConfirmationPrompt
{
    public async Task<Confirmation?> AskAsync(ConfirmationRequirement requirement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // Before the refusal below as well as in ModalDialog, so a cancelled clean reports its
        // cancellation whatever this row's tier.
        ct.ThrowIfCancellationRequested();

        // Tier 4 is not a question. Reaching here means a row was offered that §3 excludes, so the
        // honest response is to refuse rather than to present a dialog the user could say yes to.
        if (requirement.Level == ConfirmationLevel.Refused)
        {
            return null;
        }

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(DialogFactory.Paragraph(requirement.Consequence));

        TextBox? typed = null;

        if (requirement.Typing is { } typing)
        {
            typed = NewPhraseBox(typing);
            body.Children.Add(DialogFactory.Paragraph(typing.Instruction));
            body.Children.Add(typed);
        }

        var dialog = DialogFactory.Question(owner, requirement.Title, body, requirement.Verb);

        if (typed is not null)
        {
            // The phrase gates the button, and Core decides whether it matches — re-implementing the
            // comparison here is how the dialog and the executor come to disagree.
            dialog.IsPrimaryButtonEnabled = false;
            typed.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
                requirement.IsSatisfiedBy((Confirmation[])[new Confirmation(requirement.ProviderId, typed.Text)]);
        }

        var result = await ModalDialog.ShowAsync(dialog, ct);

        return result == ContentDialogResult.Primary
            ? new Confirmation(requirement.ProviderId, typed?.Text)
            : null;
    }

    private static TextBox NewPhraseBox(PhraseWording typing)
    {
        var box = new TextBox { PlaceholderText = typing.Phrase };

        AutomationProperties.SetName(box, typing.BoxName);
        return box;
    }
}
