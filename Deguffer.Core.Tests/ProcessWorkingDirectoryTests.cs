using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which environment block a working directory is read from, and what is reported where the block
/// that is current cannot be shown to be the one being read.
///
/// <para>A 32-bit process on 64-bit Windows keeps its working directory in a 32-bit block of its own.
/// The 64-bit copy beside it is never updated, and was measured reading the Windows directory for a
/// 32-bit <c>cmd.exe</c> that had been started somewhere else and then changed directory. Driven
/// against memory written here, because this machine cannot be made to show a 32-bit block that does
/// not check out.</para>
/// </summary>
public class ProcessWorkingDirectoryTests
{
    private const string Project = @"C:\Users\testuser\src\app\";
    private const string Stale = @"C:\Windows\";
    private const string CommandLine = @"C:\Windows\SysWOW64\cmd.exe";

    [Fact]
    public void AProcessOfDeguffersOwnWidthIsReadFromItsOwnBlock()
    {
        var read = ProcessWorkingDirectory.Of(FakeProcessMemory.Native(Project, CommandLine));

        Assert.Equal(new WorkingDirectoryRead(Project, false), read);
    }

    [Fact]
    public void A32BitProcessIsReadFromIts32BitBlockRatherThanTheStale64BitOne()
    {
        var read = ProcessWorkingDirectory.Of(FakeProcessMemory.Wow64(Project, Stale, CommandLine));

        Assert.Equal(new WorkingDirectoryRead(Project, false), read);
    }

    /// <summary>
    /// A command line that is not the 64-bit block's own says the 32-bit block was not found, so its
    /// directory is not believed, and the 64-bit one is not offered in its place.
    /// </summary>
    [Fact]
    public void A32BitBlockWhoseCommandLineDisagreesIsUnverifiedAndNeverTheStaleValue()
    {
        var memory = FakeProcessMemory.Wow64(Project, Stale, CommandLine, wow64CommandLine: "something else");

        Assert.Equal(WorkingDirectoryRead.Unverified, ProcessWorkingDirectory.Of(memory));
    }

    /// <summary>
    /// Both command lines empty would match without showing anything, so an empty one checks nothing.
    /// </summary>
    [Fact]
    public void A32BitBlockWithAnEmptyCommandLineIsUnverified()
    {
        var memory = FakeProcessMemory.Wow64(Project, Stale, commandLine: "");

        Assert.Equal(WorkingDirectoryRead.Unverified, ProcessWorkingDirectory.Of(memory));
    }

    [Fact]
    public void A32BitBlockWhoseParametersCannotBeFoundIsUnverified()
    {
        var memory = FakeProcessMemory.Wow64(Project, Stale, CommandLine).WithoutWow64Parameters();

        Assert.Equal(WorkingDirectoryRead.Unverified, ProcessWorkingDirectory.Of(memory));
    }

    /// <summary>
    /// A process that exits between the reads of its two blocks has no memory left to read. That is
    /// a process that could not be read, as it is for a 64-bit one, and it must not turn every table
    /// incomplete because some 32-bit program happened to exit during a scan.
    /// </summary>
    [Fact]
    public void A32BitProcessThatExitsWhileBeingReadIsUnreadRatherThanUnverified()
    {
        var memory = FakeProcessMemory.Wow64(Project, Stale, CommandLine).ExitingAtWow64Block();

        Assert.Equal(WorkingDirectoryRead.Unread, ProcessWorkingDirectory.Of(memory));
    }

    /// <summary>
    /// The 64-bit block's layout is the one the self-check proved, so a 32-bit process whose 64-bit
    /// block cannot be read is one that could not be read, not doubt about the layout.
    /// </summary>
    [Fact]
    public void A32BitProcessWhose64BitBlockCannotBeReadIsUnread()
    {
        var memory = FakeProcessMemory.Wow64(Project, Stale, CommandLine);
        memory.EnvironmentBlockAddress = 0x9000;

        Assert.Equal(WorkingDirectoryRead.Unread, ProcessWorkingDirectory.Of(memory));
    }

    [Fact]
    public void A32BitBlockWhoseDirectoryIsNotRootedIsUnverified()
    {
        var memory = FakeProcessMemory.Wow64("not a path", Stale, CommandLine);

        Assert.Equal(WorkingDirectoryRead.Unverified, ProcessWorkingDirectory.Of(memory));
    }

    /// <summary>
    /// Where Windows will not say whether the process is 32-bit, nothing says which block is current,
    /// and the 64-bit one is stale for exactly the processes it cannot tell apart.
    /// </summary>
    [Fact]
    public void AProcessWhoseWidthCannotBeAskedIsUnverified()
    {
        var memory = FakeProcessMemory.Native(Project, CommandLine);
        memory.Wow64EnvironmentBlockAddress = null;

        Assert.Equal(WorkingDirectoryRead.Unverified, ProcessWorkingDirectory.Of(memory));
    }

    /// <summary>
    /// A block Windows gives no address for is a process that could not be read, as it always was,
    /// and not doubt about the layout: that would turn every table incomplete over one process that
    /// exited while being read.
    /// </summary>
    [Fact]
    public void AProcessWithNoBlockIsUnreadRatherThanUnverified()
    {
        var memory = FakeProcessMemory.Native(Project, CommandLine);
        memory.EnvironmentBlockAddress = null;

        Assert.Equal(WorkingDirectoryRead.Unread, ProcessWorkingDirectory.Of(memory));
    }
}
