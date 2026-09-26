using Deguffer.Core.Memory;

namespace Deguffer.Testing;

/// <summary>
/// What a test says Windows reports about the shell window. This machine's own shell cannot be made
/// into one Windows will not name, and that is the answer §7.2.1 refuses on.
/// </summary>
internal sealed class FakeDesktopFacts(ShellOwner shell) : IDesktopFacts
{
    /// <summary>The ordinary desktop: a shell window, owned by the process a test names.</summary>
    public FakeDesktopFacts(int shellOwner)
        : this(ShellOwner.Is(shellOwner))
    {
    }

    /// <summary>How many times the shell window's owner was asked for, which §7.2.1 asks once.</summary>
    public int Reads { get; private set; }

    public ShellOwner ShellWindowOwner()
    {
        Reads++;
        return shell;
    }
}
