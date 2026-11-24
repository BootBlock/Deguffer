namespace Deguffer.App.Shell;

/// <summary>
/// How a copy waits for another program to let go of the clipboard, apart from the clipboard itself
/// so a test can hold it. Clipboard history and the programs that watch the clipboard open it the
/// moment its content changes, so the flush that follows setting the content routinely meets it
/// held, and it is free again within moments.
/// </summary>
internal static class ClipboardWrite
{
    /// <summary>How many times each step is tried while the clipboard is held.</summary>
    public const int Attempts = 10;

    public const string Held = "Another program is using the clipboard. Try again.";

    public const string NotKept =
        "Copied, but another program is holding the clipboard, so the text stays on it only while Deguffer is open.";

    /// <summary>
    /// Set the content, then flush it, trying each again while the clipboard is held. Null on
    /// success, or a sentence for the user.
    /// </summary>
    /// <param name="set">Sets the content, or answers false while another program holds the clipboard.</param>
    /// <param name="flush">Keeps the content after Deguffer closes, or answers false while it is held.</param>
    /// <param name="wait">The pause between tries.</param>
    public static async Task<string?> PutAsync(Func<bool> set, Func<bool> flush, Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        if (!await WhileHeldAsync(set, wait, ct))
        {
            return Held;
        }

        // The text is on the clipboard by now, so a flush that never gets through costs only its
        // staying there after Deguffer closes, and the sentence says exactly that.
        return await WhileHeldAsync(flush, wait, ct) ? null : NotKept;
    }

    private static async Task<bool> WhileHeldAsync(Func<bool> step, Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (step())
            {
                return true;
            }

            if (attempt == Attempts)
            {
                return false;
            }

            await wait(ct);
        }
    }
}
