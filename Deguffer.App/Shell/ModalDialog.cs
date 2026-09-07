using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// The one door every <see cref="ContentDialog"/> in this app opens through.
///
/// WinUI permits one dialog at a time and throws a <c>COMException</c> out of
/// <see cref="ContentDialog.ShowAsync"/> when a second one asks. Every call site is an
/// <c>async void</c> handler or an awaited command, so that throw is nobody's to catch: it reaches
/// <c>Application.UnhandledException</c> and ends the process with no window and no message. A
/// guard on the one handler that was seen to do it would leave the other three, because the rule
/// belongs to the framework rather than to any page.
///
/// Reaching a control behind an open modal is not only an automation trick — the row information
/// dialog is three quarters of the window, so what is still exposed around it depends on the
/// layout of the moment — and UI Automation can invoke a control whatever is drawn on top of it.
/// Neither is a reason to trust a call site to be unreachable.
///
/// A second request is refused rather than queued behind the first or allowed to displace it.
/// Queueing would raise a dialog once the user's attention has moved on, and a deletion
/// confirmation is the worst thing to put on screen out of context; hiding the first would answer
/// a question the user is halfway through. The refusal reports <see cref="ContentDialogResult.None"/>,
/// which is what the Close button already reports, so a blocked confirmation reads as "not
/// confirmed" at every call site and cannot authorise a deletion.
/// </summary>
internal static class ModalDialog
{
    /// <summary>
    /// Whether a dialog is on screen. A plain field with no lock because
    /// <see cref="ContentDialog.ShowAsync"/> is UI-thread-only in the first place, so every reader
    /// and writer is the one thread.
    /// </summary>
    private static bool _showing;

    /// <summary>
    /// Show <paramref name="dialog"/> and report what the user chose, or report
    /// <see cref="ContentDialogResult.None"/> without showing anything if a dialog is already open.
    /// </summary>
    internal static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_showing)
        {
            return ContentDialogResult.None;
        }

        _showing = true;

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            // Cleared when the await returns rather than from the dialog's own Closed event, which
            // fires earlier. The two orderings fail in opposite directions: clearing late can drop
            // a click made in the moment a dialog is closing, and clearing early puts the crash
            // back.
            _showing = false;
        }
    }
}
