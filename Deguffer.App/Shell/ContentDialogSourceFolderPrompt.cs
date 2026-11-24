using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Asks for a source folder through the system picker, and about a folder with a warning through a
/// dialog.
///
/// <para>The picker rather than a text box, so the path is one the user navigated to and the shell
/// confirmed exists. A source folder widens what Deguffer will delete, which is reason enough not to
/// accept a typed string.</para>
///
/// <para>A dialog rather than a sentence afterwards, because the warning is about a cost the folder's
/// own contents decide: the user knows what is in it and Deguffer does not, so it has to be read
/// before the approval. Close is the default button, so a reader who presses Enter on a dialog they
/// have not read does not consent by reflex.</para>
/// </summary>
/// <param name="owner">The page the dialog belongs to.</param>
public sealed class ContentDialogSourceFolderPrompt(FrameworkElement owner) : ISourceFolderPrompt
{
    /// <summary>Null as well where there is no window to own the picker.</summary>
    public async Task<string?> PickAsync() =>
        App.MainWindow is { } window ? await FolderDialog.ChooseAsync(window) : null;

    public async Task<bool> AcceptsAsync(SourceRootApproval approval, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(approval);

        if (approval.Consequence is not { } consequence)
        {
            return true;
        }

        var dialog = DialogFactory.Question(owner, approval.Title, DialogFactory.Paragraph(consequence), approval.ConfirmLabel);

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary;
    }
}
