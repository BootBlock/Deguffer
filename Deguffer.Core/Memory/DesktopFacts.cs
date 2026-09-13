namespace Deguffer.Core.Memory;

/// <summary>
/// What Windows says about the shell's own window.
///
/// <para>Three answers rather than two, for the reason <see cref="Answer"/> has three: §7.2.1 refuses
/// the process owning the shell window, so "there is a shell window and Windows would not say whose
/// it is" cannot read the same as "there is no shell window". The first leaves Deguffer unable to
/// tell whether the program in front of it is the desktop.</para>
/// </summary>
/// <param name="Read">
/// <see cref="Answer.Yes"/> where <paramref name="ProcessId"/> owns the shell window,
/// <see cref="Answer.No"/> where Windows reports no shell window at all, and
/// <see cref="Answer.Unreadable"/> where there is one and its owner could not be read.
/// </param>
/// <param name="ProcessId">The owner, and zero unless <paramref name="Read"/> is <see cref="Answer.Yes"/>.</param>
public readonly record struct ShellOwner(Answer Read, int ProcessId)
{
    /// <summary>Windows reports no shell window, so there is no owner to refuse or to look for.</summary>
    public static readonly ShellOwner None = new(Answer.No, 0);

    /// <summary>There is a shell window, and Windows would not say whose it is.</summary>
    public static readonly ShellOwner Unreadable = new(Answer.Unreadable, 0);

    public static ShellOwner Is(int processId) => new(Answer.Yes, processId);
}

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
    /// <summary>The process Windows reports as owning the shell window, if it will say.</summary>
    ShellOwner ShellWindowOwner();
}

/// <inheritdoc />
public sealed class DesktopFacts : IDesktopFacts
{
    public static readonly DesktopFacts Default = new(WindowCalls.Instance);

    private readonly IWindowCalls _windows;

    internal DesktopFacts(IWindowCalls windows) => _windows = windows;

    public ShellOwner ShellWindowOwner() =>
        _windows.ShellWindow() is not { } shell ? ShellOwner.None
            : _windows.ProcessOf(shell) is { } owner ? ShellOwner.Is(owner)
            : ShellOwner.Unreadable;
}
