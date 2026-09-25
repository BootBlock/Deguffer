using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two real seams behind the upgrade providers, asked only what can be asked without changing
/// the machine: how the list of what a restart will move is read, and where the Disk Cleanup host
/// refuses before Windows is asked anything.
/// </summary>
public sealed class WindowsServicingTests
{
    /// <summary>
    /// <c>PendingFileRenameOperations</c> holds pairs in the NT form, a replacement's destination is
    /// marked <c>!</c>, and a delete has an empty destination. Each path comes back once, in display
    /// form, and the empty entries are not paths at all.
    /// </summary>
    [Fact]
    public void ReadsThePathsARestartWillMoveInDisplayForm()
    {
        string[] value =
        [
            @"\??\C:\$WinREAgent\Scratch\update.wim", string.Empty,
            @"\??\C:\Windows\System32\driver.sys.new", @"!\??\C:\Windows\System32\driver.sys",
        ];

        Assert.Equal(
            [@"C:\$WinREAgent\Scratch\update.wim", @"C:\Windows\System32\driver.sys.new", @"C:\Windows\System32\driver.sys"],
            WindowsServicing.Operations(value));
    }

    [Fact]
    public void AMissingValueIsNoOperations()
    {
        Assert.Empty(WindowsServicing.Operations(null));
    }

    /// <summary>
    /// The handler resolves its registered directories against what it is given, so anything that is
    /// not the top of a drive in display form is refused before Windows is asked — a folder, a share,
    /// and the extended-length form §6.3 uses everywhere else.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"C:")]
    public void TheRealHostRefusesAnythingButTheTopOfADrive(string volume)
    {
        var outcome = DiskCleanupHandlers.Default.Run("Previous Installations", volume, CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Contains("not the top of a drive", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealHostServesNoHandlerWindowsDoesNotRegister()
    {
        Assert.False(DiskCleanupHandlers.Default.Serves("Deguffer test handler that is never registered"));
    }
}
