using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two real seams behind the upgrade providers, asked only what can be asked without changing
/// the machine: how the list of what a restart will move is read, and where the Disk Cleanup host
/// refuses before Windows is asked anything.
/// </summary>
public sealed class WindowsServicingTests
{
    private const string NeverRegistered = "Deguffer test handler that is never registered";

    /// <summary>
    /// <c>PendingFileRenameOperations</c> holds pairs in the NT form, a replacement's destination is
    /// marked <c>!</c>, and a delete has an empty destination. Each path comes back once, in display
    /// form, and the empty entries are not paths at all. A share is <c>\??\UNC\server\share</c> in
    /// the NT form, and dropping only the <c>\??\</c> would leave a path that is not fully qualified.
    /// </summary>
    [Fact]
    public void ReadsThePathsARestartWillMoveInDisplayForm()
    {
        string[] value =
        [
            @"\??\C:\$WinREAgent\Scratch\update.wim", string.Empty,
            @"\??\C:\Windows\System32\driver.sys.new", @"!\??\C:\Windows\System32\driver.sys",
            @"\??\UNC\server\share\cache\a.bin", string.Empty,
        ];

        Assert.Equal(
            [
                @"C:\$WinREAgent\Scratch\update.wim",
                @"C:\Windows\System32\driver.sys.new",
                @"C:\Windows\System32\driver.sys",
                @"\\server\share\cache\a.bin",
            ],
            WindowsServicing.Operations(value));
    }

    [Fact]
    public void AMissingValueIsNoOperations()
    {
        Assert.Empty(WindowsServicing.Operations(null));
    }

    /// <summary>
    /// Windows keeps each pending path as the program that asked for the move spelled it, so an
    /// entry may name the folder by its 8.3 alias. Compared as spelled, it read as outside the
    /// folder, and the step that deletes the folder ran. Either side may be the short one, and a
    /// sibling whose long name starts with the folder's is still outside it.
    ///
    /// <para><b>This proves nothing on a volume with 8.3 name creation disabled</b>, where the
    /// fixture falls back to the ordinary path.</para>
    /// </summary>
    [Fact]
    public void ReadsAnEntryNamedByItsShortFormAsInsideTheFolder()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("Upgrade-Leftover");
        var sibling = temp.CreateDirectory("Upgrade-Leftover-Sibling");
        var shortFolder = ShortPath.Of(folder) ?? folder;
        var shortSibling = ShortPath.Of(sibling) ?? sibling;

        Assert.True(WindowsServicing.AnyIn(folder, Pending(Path.Combine(shortFolder, "Scratch", "update.wim"))));
        Assert.True(WindowsServicing.AnyIn(shortFolder, Pending(Path.Combine(folder, "Scratch", "update.wim"))));
        Assert.False(WindowsServicing.AnyIn(folder, Pending(Path.Combine(shortSibling, "update.wim"))));
    }

    /// <summary>
    /// An entry whose alias Windows will not expand may be an alias for the folder asked about, so
    /// it counts as inside it, as an unreadable list does. The server name is reserved, so it is
    /// never reached.
    /// </summary>
    [Fact]
    public void ReadsAnEntryWhoseAliasCannotBeExpandedAsInsideTheFolder()
    {
        Assert.True(WindowsServicing.AnyIn(@"C:\$WinREAgent", Pending(@"\\server.test\share\LONGPR~1\run-1")));
        Assert.False(WindowsServicing.AnyIn(@"C:\$WinREAgent", Pending(@"C:\Windows\System32\driver.sys")));
    }

    /// <summary>
    /// The handler resolves its registered directories against what it is given, so anything that is
    /// not the top of a drive in display form is refused before Windows is asked — a folder, a share,
    /// and the extended-length form §6.3 uses everywhere else.
    ///
    /// <para>Asked of a handler Windows never registers, so a broken guard fails this test by reaching
    /// the registry lookup and saying so, and can never reach a real cleanup on this machine.</para>
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"C:")]
    public void TheRealHostRefusesAnythingButTheTopOfADrive(string volume)
    {
        var outcome = DiskCleanupHandlers.Default.Run(NeverRegistered, volume, CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Contains("not the top of a drive", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler Windows does not register is unavailable, which a plan reads as "leave the folder",
    /// never as something elevating would reveal.
    /// </summary>
    [Fact]
    public void TheRealHostCallsAHandlerWindowsDoesNotRegisterUnavailable()
    {
        var survey = DiskCleanupHandlers.Default.Survey(NeverRegistered, @"C:\", CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.Unavailable, survey.Answer);
        Assert.False(survey.MayOffer);

        // The refusal of anything but the top of a drive answers Unavailable too, so the reason is
        // what shows that C:\ passed that guard and reached the registry.
        Assert.Contains("no longer registers", survey.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>PendingFileRenameOperations</c> holding one delete of <paramref name="path"/>, in the NT
    /// form and spelled as given. Built by hand rather than from <see cref="LongPath.Extended"/>,
    /// whose <see cref="Path.GetFullPath(string)"/> expands the 8.3 alias these tests depend on.
    /// </summary>
    private static object?[] Pending(string path) =>
        [new[] { path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + path[2..] : @"\??\" + path, string.Empty }];
}
