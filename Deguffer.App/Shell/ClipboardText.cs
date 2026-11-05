using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Deguffer.App.Shell;

/// <summary>Puts text on the Windows clipboard for every page that offers a copy.</summary>
public static class ClipboardText
{
    /// <summary>CLIPBRD_E_CANT_OPEN: Windows' answer while another program holds the clipboard open.</summary>
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    /// <summary>
    /// Put <paramref name="text"/> on the clipboard. Null on success, or a sentence for the user.
    ///
    /// <para>Flushed, so the text is still there to paste after Deguffer closes.</para>
    /// </summary>
    public static string? Put(string text)
    {
        var package = new DataPackage();
        package.SetText(text);

        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return null;
        }
        catch (COMException ex) when (ex.HResult == ClipboardCannotOpen)
        {
            // Another program has the clipboard open. It lets go within moments, so the answer is to
            // try again rather than to give up.
            return "Another program is using the clipboard. Try again.";
        }
    }
}
