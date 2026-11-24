using Deguffer.Core.InstalledApps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>Asks an Installed apps question (§7.3) in a dialog. The words are Core's.</summary>
public sealed class ContentDialogInstalledAppsConfirmation(FrameworkElement owner) : IInstalledAppsConfirmation
{
    public async Task<bool> AskAsync(InstalledAppsPrompt prompt, CancellationToken ct)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(DialogFactory.Paragraph(prompt.Consequence));
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 240,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, prompt.Items),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        var dialog = DialogFactory.Question(owner, prompt.Title, content, prompt.ConfirmLabel);

        return await ModalDialog.ShowAsync(dialog, ct) == ContentDialogResult.Primary;
    }
}
