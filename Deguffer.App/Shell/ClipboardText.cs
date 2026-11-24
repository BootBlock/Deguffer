using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Deguffer.App.Shell;

/// <summary>Puts text on the Windows clipboard for every page that offers a copy.</summary>
public static class ClipboardText
{
    /// <summary>CLIPBRD_E_CANT_OPEN: Windows' answer while another program holds the clipboard open.</summary>
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Put <paramref name="text"/> on the clipboard. Null on success, or a sentence for the user.
    ///
    /// <para>Flushed, so the text is still there to paste after Deguffer closes. Called on the UI
    /// thread, which the clipboard requires, and each wait between tries returns to it without
    /// blocking it (<see cref="ClipboardWrite"/>).</para>
    /// </summary>
    public static Task<string?> PutAsync(string text, CancellationToken ct)
    {
        var package = new DataPackage();
        package.SetText(text);

        return ClipboardWrite.PutAsync(
            () => Unheld(() => Clipboard.SetContent(package)),
            () => Unheld(Clipboard.Flush),
            token => Task.Delay(RetryInterval, token),
            ct);
    }

    private static bool Unheld(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (COMException ex) when (ex.HResult == ClipboardCannotOpen)
        {
            // Another program has the clipboard open, which it lets go of within moments.
            return false;
        }
    }
}
