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
public sealed class ContentDialogConfirmationPrompt(XamlRoot xamlRoot) : IConfirmationPrompt
{
    public async Task<Confirmation?> AskAsync(ConfirmationRequirement requirement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // Checked before the dialog is built rather than only through the registration below: a
        // token already cancelled would otherwise fire Hide against a dialog ShowAsync has not yet
        // opened, leaving a modal up that the cancel path can no longer take down.
        ct.ThrowIfCancellationRequested();

        // Tier 4 is not a question. Reaching here means a row was offered that §3 excludes, so the
        // honest response is to refuse rather than to present a dialog the user could say yes to.
        if (requirement.Level == ConfirmationLevel.Refused)
        {
            return null;
        }

        var typed = requirement.Level == ConfirmationLevel.TypedPhrase ? NewPhraseBox(requirement) : null;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Delete {requirement.ProviderName}?",
            Content = NewBody(requirement, typed),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (typed is not null)
        {
            // The phrase gates the button, and Core decides whether it matches — re-implementing the
            // comparison here is how the dialog and the executor come to disagree.
            dialog.IsPrimaryButtonEnabled = false;
            typed.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
                requirement.IsSatisfiedBy([new Confirmation(requirement.ProviderId, typed.Text)]);
        }

        using var registration = ct.Register(dialog.Hide);

        var result = await dialog.ShowAsync();

        return result == ContentDialogResult.Primary
            ? new Confirmation(requirement.ProviderId, typed?.Text)
            : null;
    }

    private static TextBox NewPhraseBox(ConfirmationRequirement requirement)
    {
        var box = new TextBox { PlaceholderText = requirement.RequiredPhrase };

        AutomationProperties.SetName(box, $"Type {requirement.RequiredPhrase} to confirm deletion");
        return box;
    }

    private static StackPanel NewBody(ConfirmationRequirement requirement, TextBox? typed)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = requirement.Consequence,
            TextWrapping = TextWrapping.WrapWholeWords,
        });

        if (typed is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Type “{requirement.RequiredPhrase}” to confirm.",
                TextWrapping = TextWrapping.WrapWholeWords,
            });

            panel.Children.Add(typed);
        }

        return panel;
    }
}
