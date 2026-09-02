using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows of the process table count as evidence, and specifically which one does not.
///
/// Deguffer has to leave itself out — it is normally started from inside a developer's own folders,
/// and on this repository from inside the very source tree it is asked to look at — but "itself" has
/// to mean <em>this process</em> and nothing else. Identifying itself by image path instead makes
/// the answer depend on how the app is hosted: a framework-dependent build runs under
/// <c>dotnet.exe</c>, and every other <c>dotnet.exe</c> on the machine then shares the identity,
/// including a build in flight. That is the one process the veto exists to catch, so the filter
/// would have disarmed itself, silently, on a change to a build property in another project.
///
/// <para>Driven against a table written here rather than against the machine's own, because the
/// case that matters cannot be staged: it needs a second process at this one's exact image path,
/// and a test cannot start one.</para>
/// </summary>
public class ProcessTableFilterTests
{
    private static ProcessTable TableOf(params RunningProcess[] processes) =>
        new(processes, CurrentDirectoriesReadable: true);

    /// <summary>
    /// The running process this test is part of is left out, which is the whole point of the filter.
    /// </summary>
    [Fact]
    public void ThisProcessIsNotEvidence()
    {
        var table = TableOf(
            new RunningProcess(Environment.ProcessId, "self", Environment.ProcessPath, @"C:\Users\testuser\src\app"));

        Assert.Empty(LiveTreeInspector.Filtered(table).Processes);
    }

    /// <summary>
    /// Another process that merely runs the same executable is still evidence.
    ///
    /// This is a framework-dependent Deguffer's own situation exactly: its image path is the shared
    /// host, and a build running under that same host is a different process doing real work in the
    /// developer's tree. Dropping it would report a live project as idle, which is the one wrong
    /// answer that costs somebody their work.
    /// </summary>
    [Fact]
    public void AnotherProcessSharingThisOnesImagePathIsStillEvidence()
    {
        var other = new RunningProcess(
            Environment.ProcessId + 1,
            "dotnet",
            Environment.ProcessPath,
            @"C:\Users\testuser\src\app");

        var filtered = LiveTreeInspector.Filtered(TableOf(other));

        Assert.Equal(other, Assert.Single(filtered.Processes));
    }

    /// <summary>
    /// A row whose image path could not be read is still evidence, and so is one whose working
    /// directory could not be.
    ///
    /// Both are ordinary on an unelevated run: <c>QueryFullProcessImageName</c> and the environment
    /// block are refused independently, and a process answers for one question while staying silent
    /// on the other. The inspector reads the two separately for that reason, so a filter that
    /// quietly dropped a half-answered row would take away the strongest signal the veto has — a
    /// build in flight, named by its working directory alone. Written because the identity rule
    /// alone does not force this: a filter reading <c>p.ImagePath is { } &amp;&amp; p.Id != …</c>, which is
    /// the idiom used one method away, satisfies every other test here and loses those rows.
    /// </summary>
    [Fact]
    public void ARowWithAFieldThatCouldNotBeReadIsStillEvidence()
    {
        var table = TableOf(
            new RunningProcess(4321, "msbuild", null, @"C:\Users\testuser\src\app"),
            new RunningProcess(4325, "python", @"C:\Users\testuser\src\app\.venv\Scripts\python.exe", null));

        var filtered = LiveTreeInspector.Filtered(table);

        Assert.Equal(["msbuild", "python"], filtered.Processes.Select(p => p.Name));
    }

    /// <summary>
    /// Everything else is passed through untouched, and the incompleteness flag with it — a filter
    /// that quietly turned an incomplete table into a complete one would claim the working
    /// directories had been read when they had not.
    /// </summary>
    [Fact]
    public void EveryOtherProcessAndTheCompletenessOfTheTableSurvive()
    {
        var table = new ProcessTable(
            [
                new RunningProcess(Environment.ProcessId, "self", Environment.ProcessPath, null),
                new RunningProcess(4321, "devenv", @"C:\Program Files\editor\devenv.exe", @"C:\Users\testuser\src\app"),
            ],
            CurrentDirectoriesReadable: false);

        var filtered = LiveTreeInspector.Filtered(table);

        Assert.Equal("devenv", Assert.Single(filtered.Processes).Name);
        Assert.False(filtered.CurrentDirectoriesReadable);
    }
}
