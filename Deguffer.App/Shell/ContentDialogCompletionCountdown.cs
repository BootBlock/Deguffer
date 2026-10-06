using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// Shows a <see cref="CompletionCountdown"/> in a dialog and ticks it once a second. The words and
/// the timing are Core's.
/// </summary>
internal static class ContentDialogCompletionCountdown
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>
    /// True where the count ran out or the user chose to act now, with nothing in
    /// <paramref name="running"/> running at that moment. False where they cancelled, or where another
    /// dialog was already open and nothing could be asked: a countdown nobody could see must not end
    /// their session.
    /// </summary>
    public static async Task<bool> AskAsync(
        CompletionCountdown countdown,
        RunningActions running,
        XamlRoot xamlRoot,
        ElementTheme theme)
    {
        if (ModalDialog.IsShowing)
        {
            return false;
        }

        var sentence = new TextBlock
        {
            Text = countdown.Sentence(running.Current),
            TextWrapping = TextWrapping.WrapWholeWords,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,

            // The popup layer does not inherit the window root's theme.
            RequestedTheme = theme,
            Title = countdown.Title,
            Content = sentence,
            PrimaryButtonText = countdown.ActNowLabel,
            CloseButtonText = countdown.CancelLabel,
            IsPrimaryButtonEnabled = CompletionCountdown.MayActNow(running.Current),

            // Cancel is the default, so a keystroke meant for something else leaves the machine alone.
            DefaultButton = ContentDialogButton.Close,
        };

        var due = false;
        var timer = new DispatcherTimer { Interval = OneSecond };

        timer.Tick += (_, _) =>
        {
            var now = running.Current;

            if (countdown.Tick(now))
            {
                due = true;
                timer.Stop();
                dialog.Hide();
                return;
            }

            sentence.Text = countdown.Sentence(now);
            dialog.IsPrimaryButtonEnabled = CompletionCountdown.MayActNow(now);
        };

        timer.Start();

        try
        {
            var result = await ModalDialog.ShowAsync(dialog);

            // Asked again as it closes. An action can begin between the last tick and a press of the
            // button, and acting then would end the process under it.
            return (due || result == ContentDialogResult.Primary)
                && CompletionCountdown.MayActNow(running.Current);
        }
        finally
        {
            timer.Stop();
        }
    }
}
