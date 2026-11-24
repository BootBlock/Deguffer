using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Deguffer.App.Shell;

/// <summary>Puts text on the Windows clipboard for every page that offers a copy.</summary>
public static class ClipboardText
{
    /// <summary>CLIPBRD_E_CANT_OPEN: Windows' answer while another program holds the clipboard open.</summary>
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    /// <summary>
    /// How many times each step is tried while the clipboard is held. Clipboard history and the
    /// programs that watch the clipboard open it the moment its content changes, so the flush that
    /// follows setting the content routinely meets it held, and it is free again within moments.
    /// </summary>
    private const int Attempts = 10;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Put <paramref name="text"/> on the clipboard. Null on success, or a sentence for the user.
    ///
    /// <para>Flushed, so the text is still there to paste after Deguffer closes. Called on the UI
    /// thread, which the clipboard requires, and each wait between tries returns to it without
    /// blocking it.</para>
    /// </summary>
    public static async Task<string?> PutAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        if (!await WhileHeldAsync(() => Clipboard.SetContent(package)))
        {
            return "Another program is using the clipboard. Try again.";
        }

        // The text is on the clipboard by now, so a flush that never gets through costs only its
        // staying there after Deguffer closes, and the sentence says exactly that.
        return await WhileHeldAsync(Clipboard.Flush)
            ? null
            : "Copied, but another program is holding the clipboard, so the text stays on it only while Deguffer is open.";
    }

    private static async Task<bool> WhileHeldAsync(Action step)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (TryOnce(step))
            {
                return true;
            }

            if (attempt == Attempts)
            {
                return false;
            }

            await Task.Delay(RetryInterval);
        }
    }

    private static bool TryOnce(Action step)
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
