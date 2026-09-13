namespace Deguffer.Core.Memory;

/// <summary>
/// What Windows says about the desktop rather than about one process.
///
/// <para>Apart from <see cref="IProcessFactSource"/> because it is not a fact about a process anyone
/// picked: §7.2.1 refuses the shell, and <c>GetShellWindow</c> is what names it exactly, since the
/// shell is only "usually explorer.exe". The same answer is what §5.6 looks for again when the watch
/// ends, so it is asked once and used twice rather than asked in two ways that could disagree.</para>
/// </summary>
public interface IDesktopFacts
{
    /// <summary>
    /// The process Windows reports as owning the shell window, or null where there is no shell window
    /// or Windows would not say whose it is.
    /// </summary>
    int? ShellWindowOwner();
}

/// <inheritdoc />
public sealed class DesktopFacts : IDesktopFacts
{
    public static readonly DesktopFacts Default = new(WindowCalls.Instance);

    private readonly IWindowCalls _windows;

    internal DesktopFacts(IWindowCalls windows) => _windows = windows;

    public int? ShellWindowOwner() =>
        _windows.ShellWindow() is { } shell ? _windows.ProcessOf(shell) : null;
}
