using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The shell window's owner, as a test says Windows reports it. This machine's own shell cannot be
/// made into one Windows will not name.
/// </summary>
internal sealed class FakeDesktopFacts(int? shellOwner = null) : IDesktopFacts
{
    public int Reads { get; private set; }

    public int? ShellWindowOwner()
    {
        Reads++;
        return shellOwner;
    }
}
