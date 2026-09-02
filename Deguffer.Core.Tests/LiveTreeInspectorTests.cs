using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The live-tree veto, exercised against real live trees rather than against a fake.
///
/// A fake cannot establish that the Restart Manager answers without elevation, that it refuses a
/// directory, or that another process's working directory is readable at all. Those are claims about
/// this machine, and the safety rule they carry — never delete a build directory somebody is using —
/// is worth nothing if they are wrong. So each test here starts a real second process, makes it hold
/// a real path, and asks.
///
/// The helper processes are ordinary Windows programs started with a long wait, and each is stopped
/// in a <c>finally</c>. Nothing here needs elevation.
/// </summary>
public sealed class LiveTreeInspectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A process whose working directory is inside the project makes the build directory live.
    ///
    /// This is the case a process-name check cannot reach and the one that matters most: a build in
    /// flight, or an editor with the solution open. Visual Studio was observed here — its working
    /// directory is the solution folder, and the process actually holding files inside <c>.vs</c> is
    /// a service host with a different name entirely.
    /// </summary>
    [Fact]
    public void ADirectoryIsLiveWhileAnotherProcessIsWorkingInTheProject()
    {
        var project = _temp.CreateDirectory("crate");
        var target = _temp.CreateDirectory("crate", "target");

        using var busy = StartWaiting(project);

        var findings = new LiveTreeInspector().FindLive([new LiveTreeQuery(target, project)]);

        Assert.True(findings.Complete);
        Assert.True(findings.IsLive(target));
        Assert.Contains(findings.Live.Single().Holders, h => h.Contains("crate", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same project with nothing running in it is not live. Without this the test above passes
    /// on a rule that answers "live" to everything.
    /// </summary>
    [Fact]
    public void ADormantProjectIsNotLive()
    {
        var project = _temp.CreateDirectory("dormant");
        var target = _temp.CreateDirectory("dormant", "target");

        var findings = new LiveTreeInspector().FindLive([new LiveTreeQuery(target, project)]);

        Assert.True(findings.Complete);
        Assert.False(findings.IsLive(target));
        Assert.Empty(findings.Live);
    }

    /// <summary>
    /// A running program inside the directory makes it live — a <c>.venv</c> whose interpreter is
    /// running, which no lock file and no sibling project would reveal.
    /// </summary>
    [Fact]
    public void ADirectoryIsLiveWhileAProgramInsideItIsRunning()
    {
        var project = _temp.CreateDirectory("app");
        var venv = _temp.CreateDirectory("app", ".venv", "Scripts");
        var copied = Path.Combine(venv, "interpreter.exe");
        File.Copy(WaitingProgram, copied);

        using var running = StartWaiting(_temp.Path, copied);

        var findings = new LiveTreeInspector().FindLive(
            [new LiveTreeQuery(Path.Combine(project, ".venv"), project)]);

        Assert.True(findings.IsLive(Path.Combine(project, ".venv")));
    }

    /// <summary>
    /// A declared lock file that something holds open makes the directory live, which is Unity's
    /// <c>UnityLockfile</c> and Visual Studio's <c>.suo</c>.
    ///
    /// The Restart Manager answers this unelevated — established here, because the whole veto rests
    /// on it and §6.3 makes an unelevated run the ordinary one.
    /// </summary>
    [Fact]
    public void ADirectoryIsLiveWhileItsDeclaredLockFileIsHeldOpen()
    {
        var project = _temp.CreateDirectory("unity");
        var library = _temp.CreateDirectory("unity", "Library");
        var lockFile = Path.Combine(library, "UnityLockfile");
        File.WriteAllText(lockFile, string.Empty);

        using var holder = File.Open(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var findings = new LiveTreeInspector().FindLive(
            [new LiveTreeQuery(library, project, ["UnityLockfile"])]);

        Assert.True(findings.Complete);
        Assert.True(findings.IsLive(library));
    }

    /// <summary>
    /// A lock file left behind by a crashed editor is not evidence of anything. Existence is the
    /// weaker test, and taking it would veto every project whose tool once died badly — for ever.
    /// </summary>
    [Fact]
    public void ALockFileNothingHoldsOpenDoesNotMakeADirectoryLive()
    {
        var project = _temp.CreateDirectory("unity");
        var library = _temp.CreateDirectory("unity", "Library");
        File.WriteAllText(Path.Combine(library, "UnityLockfile"), string.Empty);

        var findings = new LiveTreeInspector().FindLive(
            [new LiveTreeQuery(library, project, ["UnityLockfile"])]);

        Assert.False(findings.IsLive(library));
    }

    /// <summary>
    /// A declared lock file that is not there costs no session and produces no answer either way.
    /// Unity removes <c>UnityLockfile</c> when the editor closes, so this is the ordinary case.
    /// </summary>
    [Fact]
    public void AnAbsentLockFileIsNotAnUnansweredQuestion()
    {
        var project = _temp.CreateDirectory("unity");
        var library = _temp.CreateDirectory("unity", "Library");

        var findings = new LiveTreeInspector().FindLive(
            [new LiveTreeQuery(library, project, ["UnityLockfile"])]);

        Assert.True(findings.Complete);
        Assert.Empty(findings.Live);
    }

    /// <summary>
    /// One project being live says nothing about its neighbour. The veto has to be per directory,
    /// or the first busy project on a disk would take every other one off the list with it.
    /// </summary>
    [Fact]
    public void OnlyTheProjectInUseIsVetoed()
    {
        var busyProject = _temp.CreateDirectory("busy");
        var busyTarget = _temp.CreateDirectory("busy", "target");
        var idleProject = _temp.CreateDirectory("idle");
        var idleTarget = _temp.CreateDirectory("idle", "target");

        using var busy = StartWaiting(busyProject);

        var findings = new LiveTreeInspector().FindLive(
        [
            new LiveTreeQuery(busyTarget, busyProject),
            new LiveTreeQuery(idleTarget, idleProject),
        ]);

        Assert.True(findings.IsLive(busyTarget));
        Assert.False(findings.IsLive(idleTarget));
    }

    /// <summary>
    /// A sibling directory whose name merely starts with the project's is not the project. Prefix
    /// matching without a separator would make <c>crate-old</c> vouch for <c>crate</c>, and a veto
    /// that fires on the wrong directory is as wrong as one that does not fire at all.
    /// </summary>
    [Fact]
    public void AProjectWhoseNameIsAPrefixOfAnothersIsNotConfusedWithIt()
    {
        var project = _temp.CreateDirectory("crate");
        var target = _temp.CreateDirectory("crate", "target");
        var lookalike = _temp.CreateDirectory("crate-old");

        using var busy = StartWaiting(lookalike);

        var findings = new LiveTreeInspector().FindLive([new LiveTreeQuery(target, project)]);

        Assert.False(findings.IsLive(target));
    }

    /// <summary>
    /// §6.3. A project past <c>MAX_PATH</c> is still answered for, rather than throwing or silently
    /// reporting dormant — the second of which would hand a live tree to a deletion.
    /// </summary>
    [Fact]
    public void ALongPathIsStillAnswered()
    {
        var deep = Path.Combine(_temp.Path, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
        Directory.CreateDirectory(LongPath.Extended(deep));
        var target = Path.Combine(deep, "target");
        Directory.CreateDirectory(LongPath.Extended(target));

        Assert.True(deep.Length > 260);

        var findings = new LiveTreeInspector().FindLive([new LiveTreeQuery(target, deep, ["build.lock"])]);

        Assert.True(findings.Complete);
        Assert.False(findings.IsLive(target));
    }

    /// <summary>
    /// The snapshot is taken once and dropped on <see cref="ILiveTreeInspector.Invalidate"/>, the
    /// same contract every other collaborator in a planning pass has.
    /// </summary>
    [Fact]
    public void TheProcessTableIsReadAgainAfterInvalidation()
    {
        var project = _temp.CreateDirectory("late");
        var target = _temp.CreateDirectory("late", "target");
        var inspector = new LiveTreeInspector();

        Assert.False(inspector.FindLive([new LiveTreeQuery(target, project)]).IsLive(target));

        using var busy = StartWaiting(project);

        // Still the old snapshot, so the process started a moment ago is not in it.
        Assert.False(inspector.FindLive([new LiveTreeQuery(target, project)]).IsLive(target));

        inspector.Invalidate();

        Assert.True(inspector.FindLive([new LiveTreeQuery(target, project)]).IsLive(target));
    }

    /// <summary>
    /// A program that waits without reading its console, so it can be started with a chosen working
    /// directory and left running for the length of one test.
    /// </summary>
    private static string WaitingProgram =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");

    private static WaitingProcess StartWaiting(string workingDirectory, string? program = null)
    {
        var start = new ProcessStartInfo(program ?? WaitingProgram)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("120");
        start.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(start)!;

        // The process table is read from another thread's point of view, and a process that has not
        // finished starting has no environment block to read yet.
        SpinWait.SpinUntil(() => TableSees(process.Id), TimeSpan.FromSeconds(10));

        return new WaitingProcess(process);
    }

    private static bool TableSees(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && process.MainWindowHandle >= 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class WaitingProcess(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone. Nothing to stop.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
