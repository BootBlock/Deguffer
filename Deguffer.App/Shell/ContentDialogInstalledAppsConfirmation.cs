using Deguffer.Core.InstalledApps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>Asks an Installed apps question (§7.3) in a dialog. The words are Core's.</summary>
public sealed class ContentDialogInstalledAppsConfirmation(FrameworkElement owner) : IInstalledAppsConfirmation
{
    public async Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct)
    {
        var dialog = DialogFactory.Question(owner, prompt.Title, DialogFactory.Listing(prompt.Consequence, prompt.Items), prompt.ConfirmLabel);

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary;
    }
}
