namespace Deguffer.App.Shell;

/// <summary>
/// The hand-offs to Windows that a page's row menu makes and that change nothing: show a file in File
/// Explorer, open its properties, and copy text. Each returns null on success, or a sentence for the
/// user.
///
/// <para>A record of delegates rather than calls to <see cref="ShellActions"/> and
/// <see cref="ClipboardText"/>, so a view-model's tests can see what it handed over without a File
/// Explorer window or the real clipboard.</para>
/// </summary>
/// <param name="Reveal">Show a file selected in its folder.</param>
/// <param name="Properties">Open a file's Windows properties.</param>
/// <param name="Copy">Put text on the clipboard.</param>
public sealed record ShellHandoff(
    Func<string, string?> Reveal,
    Func<string, string?> Properties,
    Func<string, string?> Copy)
{
    public static ShellHandoff Windows { get; } = new(
        path => ShellActions.Reveal(path, isDirectory: false),
        ShellActions.Properties,
        ClipboardText.Put);
}
