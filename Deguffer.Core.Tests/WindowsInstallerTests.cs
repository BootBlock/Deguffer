using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the real Windows Installer calls answer. Both only read registration, so asking the test
/// machine changes nothing. A fake would be asserting its own reply, and the call's marshalling is
/// what can go wrong.
/// </summary>
public sealed class WindowsInstallerTests
{
    [Fact]
    public void ACodeNothingRegisteredIsUnknown()
    {
        Assert.Equal(InstallerProductState.Unknown, WindowsInstaller.Default.QueryProductState(Guid.NewGuid()));
    }

    /// <summary>
    /// A listing that stops with an error is reported as incomplete, and the stale rule then proves
    /// nothing from it. So a marshalling mistake would pass silently everywhere except here.
    /// </summary>
    [Fact]
    public void ThePatchesAreListedToTheEnd()
    {
        Assert.True(WindowsInstaller.Default.QueryPatches().IsComplete);
    }
}
