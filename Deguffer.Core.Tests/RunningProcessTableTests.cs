using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How one process's working directory reaches the veto, and how a directory that could not be
/// trusted reaches <see cref="LiveTreeFindings.Complete"/>: a safeguard that could not look must not
/// read as one that found nothing.
/// </summary>
public class RunningProcessTableTests
{
    private const string Project = @"C:\Users\testuser\src\app";
    private const string Other = @"C:\Users\testuser\src\other";
    private const string Stale = @"C:\Windows\";
    private const string ShellLine = @"C:\Windows\SysWOW64\cmd.exe";

    private static readonly LiveTreeQuery Build = new(Path.Combine(Project, "bin"), Project);

    [Fact]
    public void A32BitShellWorkingInTheProjectMakesItsBuildOutputLive()
    {
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(Shell(FakeProcessMemory.Wow64(Project + @"\", Stale, ShellLine)));

        var findings = new LiveTreeInspector(calls).FindLive([Build]);

        Assert.True(findings.Complete);
        Assert.True(findings.IsLive(Build.Directory));
    }

    /// <summary>
    /// The 32-bit block could not be checked, so the shell's directory is unknown. The stale 64-bit
    /// value is not reported in its place, the findings say they are incomplete, and every other
    /// process's directory is still read.
    /// </summary>
    [Fact]
    public void A32BitBlockThatCannotBeCheckedTurnsTheFindingsIncompleteAndKeepsTheRest()
    {
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(Shell(FakeProcessMemory.Wow64(Project + @"\", Stale, ShellLine, wow64CommandLine: "garbage")))
            .With(new FakeListedProcess
            {
                Id = 4322,
                Name = "msbuild",
                CommandLine = "msbuild.exe",
                Memory = FakeProcessMemory.Native(Other + @"\", "msbuild.exe"),
            });

        var findings = new LiveTreeInspector(calls).FindOccupiedDirectories();

        Assert.False(findings.Complete);
        Assert.DoesNotContain(findings.Live, place => place.Holders.Any(h => h.StartsWith("cmd ", StringComparison.Ordinal)));
        Assert.Equal(Other, Assert.Single(findings.Live).Directory);
    }

    /// <summary>
    /// A process whose memory could not be read at all is not a doubt about the layout, as it never
    /// was: it is one of the processes the veto cannot see, which the design records.
    /// </summary>
    [Fact]
    public void AProcessWhoseMemoryIsRefusedLeavesTheFindingsComplete()
    {
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess { Id = 4323, Name = "elevated", CommandLine = "elevated.exe" });

        var findings = new LiveTreeInspector(calls).FindOccupiedDirectories();

        Assert.True(findings.Complete);
        Assert.Empty(findings.Live);
    }

    /// <summary>
    /// The self-check reads a directory that is not this process's own, so the offset no longer
    /// describes this Windows and no working directory is read at all.
    /// </summary>
    [Fact]
    public void ALayoutThatFailsTheSelfCheckReadsNoWorkingDirectories()
    {
        var calls = new FakeProcessTableCalls()
            .With(new FakeListedProcess
            {
                Id = Environment.ProcessId,
                Name = "Deguffer",
                CommandLine = "Deguffer.exe",
                Memory = FakeProcessMemory.Native(@"C:\Users\testuser\elsewhere\", "Deguffer.exe"),
            })
            .With(Shell(FakeProcessMemory.Native(Project + @"\", ShellLine)));

        var findings = new LiveTreeInspector(calls).FindLive([Build]);

        Assert.False(findings.Complete);
        Assert.False(findings.IsLive(Build.Directory));
    }

    private static FakeListedProcess Shell(FakeProcessMemory memory) => new()
    {
        Id = 4321,
        Name = "cmd",
        CommandLine = ShellLine,
        Memory = memory,
    };
}
