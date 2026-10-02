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
    /// A process that cannot be opened at all, one of another account or a protected one, is left out
    /// as it always was, and the processes that can be opened are still read, image path included.
    /// </summary>
    [Fact]
    public void AProcessThatCannotBeOpenedIsLeftOutAndTheRestAreRead()
    {
        var otherBuild = new LiveTreeQuery(Path.Combine(Other, "bin"), Other);

        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess
            {
                Id = 4324,
                Name = "service",
                OpenRefused = true,
                ImagePath = Path.Combine(otherBuild.Directory, "service.exe"),
            })
            .With(new FakeListedProcess
            {
                Id = 4325,
                Name = "app",
                ImagePath = Path.Combine(Build.Directory, "app.exe"),
            });

        var findings = new LiveTreeInspector(calls).FindLive([Build, otherBuild]);

        Assert.True(findings.Complete);
        Assert.True(findings.IsLive(Build.Directory));
        Assert.False(findings.IsLive(otherBuild.Directory));
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

    /// <summary>
    /// A path a program was started with that cannot be made canonical is left out and says so,
    /// rather than being compared as it arrived. A segment that carries a <c>~</c> inside a folder
    /// Windows will not list may be an alias for the very entry being asked about, and read raw it
    /// would match nothing, which is the answer that lets the entry be removed.
    ///
    /// <para>The <c>~</c> is part of a real name rather than an 8.3 alias, so this discriminates on
    /// a volume with short names disabled too: the refusal is what is under test.</para>
    /// </summary>
    [Fact]
    public void AnArgumentThatCannotBeMadeCanonicalTurnsTheChildFindingsIncomplete()
    {
        using var temp = new TempDirectory();
        var scratch = temp.CreateDirectory("Temp");
        var hidden = temp.CreateDirectory("Temp", "Locked", "run~1");
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess
            {
                Id = 4326,
                Name = "runner",
                CommandLine = $"runner.exe --log-file={Path.Combine(hidden, "out.log")}",
            });

        using var denied = new DeniedDirectory(Path.Combine(scratch, "Locked"));

        var findings = new LiveTreeInspector(calls).FindLiveChildren([scratch]);

        Assert.False(findings.Complete);
    }

    /// <summary>
    /// The same refusal in a working directory turns the working-directory findings incomplete, so
    /// a directory nothing could be compared against is not taken as nobody working there.
    /// </summary>
    [Fact]
    public void AWorkingDirectoryThatCannotBeMadeCanonicalTurnsTheFindingsIncomplete()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateDirectory("Locked", "run~1");
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(Shell(FakeProcessMemory.Native(hidden + @"\", ShellLine)));

        using var denied = new DeniedDirectory(Path.Combine(temp.Path, "Locked"));

        var findings = new LiveTreeInspector(calls).FindOccupiedDirectories();

        Assert.False(findings.Complete);
        Assert.Empty(findings.Live);
    }

    /// <summary>
    /// An executable whose path cannot be made canonical turns the findings incomplete, so a
    /// program that may be running from inside a build directory is not taken as no program.
    /// </summary>
    [Fact]
    public void AnExecutableThatCannotBeMadeCanonicalTurnsTheFindingsIncomplete()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateDirectory("Locked", "run~1");
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess { Id = 4327, Name = "app", ImagePath = Path.Combine(hidden, "app.exe") });

        using var denied = new DeniedDirectory(Path.Combine(temp.Path, "Locked"));

        var inspector = new LiveTreeInspector(calls);

        Assert.False(inspector.FindLive([new LiveTreeQuery(hidden, hidden)]).Complete);
        Assert.False(inspector.FindOccupiedDirectories().Complete);
    }

    /// <summary>
    /// A scratch folder that cannot be made canonical is still compared, and the answer says it may
    /// have missed a program using one of its entries.
    /// </summary>
    [Fact]
    public void AFolderAskedAboutThatCannotBeMadeCanonicalTurnsTheChildFindingsIncomplete()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateDirectory("Locked", "temp~1");
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);

        using var denied = new DeniedDirectory(Path.Combine(temp.Path, "Locked"));

        var findings = new LiveTreeInspector(calls).FindLiveChildren([hidden]);

        Assert.False(findings.Complete);
    }

    private static FakeListedProcess Shell(FakeProcessMemory memory) => new()
    {
        Id = 4321,
        Name = "cmd",
        CommandLine = ShellLine,
        Memory = memory,
    };
}
