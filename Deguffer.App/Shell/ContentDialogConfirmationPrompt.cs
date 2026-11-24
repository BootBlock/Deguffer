using Deguffer.App.ViewModels;
using Deguffer.App.Views;
using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Renders a clean's confirmation as one dialog. Every judgement here (what is listed, what each row
/// needs, the figure, whether the button may be pressed) comes from the <see cref="CleanConfirmation"/>
/// through <see cref="CleanConfirmationViewModel"/>; this type chooses only how it looks.
/// </summary>
public sealed class ContentDialogConfirmationPrompt(FrameworkElement owner) : IConfirmationPrompt
{
    public async Task<IReadOnlyList<CleanConfirmationReply>?> AskAsync(
        CleanConfirmation confirmation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        var model = new CleanConfirmationViewModel(confirmation);

        // Close stays the default button (DialogFactory), so Enter in a typed box never starts a clean.
        var dialog = DialogFactory.Question(
            owner, model.Title, new CleanConfirmationView(model), model.ConfirmLabel);
        dialog.IsPrimaryButtonEnabled = model.CanConfirm;

        // The dialog's own title and buttons are not in its content, so they follow the model here
        // rather than through a binding. Subscribed before the dialog opens and owned by this call, so
        // nothing the dialog does to its content's Loaded and Unloaded can drop it.
        model.PropertyChanged += (_, _) =>
        {
            dialog.Title = model.Title;
            dialog.PrimaryButtonText = model.ConfirmLabel;
            dialog.IsPrimaryButtonEnabled = model.CanConfirm;
        };

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary
            ? model.Replies
            : null;
    }
}
