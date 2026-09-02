using System.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The live-tree veto, exercised against real live trees rather than against a fake.
///
/// A fake cannot establish that the Restart Manager answers without elevation, or that another
/// process's working directory is readable at all. Those are claims about this machine, and the
/// safety rule they carry — never delete a build directory somebody is using — is worth nothing if
/// they are wrong. So each test here starts a real second process, makes it hold a real path, and
/// asks.
///
/// One claim the code makes is deliberately not established here: that the Restart Manager refuses a
/// directory outright. It does, and that is why a provider has to name the file its tool locks — but
/// <c>RestartManager</c> is internal to Core with one caller, and what is testable from outside is
/// the consequence rather than the refusal. <see cref="ADeclaredLockFileThatIsADirectoryIsNeverAsked"/>
/// covers that consequence.
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

        using var busy = StartWaiting(project, new LiveTreeQuery(target, project));

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

        using var running = StartWaiting(
            _temp.Path,
            new LiveTreeQuery(Path.Combine(project, ".venv"), project),
            copied);

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
    /// A declared lock file that is not there is the ordinary case — Unity removes
    /// <c>UnityLockfile</c> when the editor closes — and it must not turn the answer into "could not
    /// tell". A plan that warned about every dormant project would train the user past the warning
    /// that matters.
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
    /// A declared lock-file name that turns out to be a directory is never handed to the Restart
    /// Manager, which refuses a directory outright with an access-denied result. Passing one would
    /// turn an ordinary project into "Deguffer could not check whether this is in use" — a warning
    /// with nothing behind it, on a plan that is in fact fine.
    /// </summary>
    [Fact]
    public void ADeclaredLockFileThatIsADirectoryIsNeverAsked()
    {
        var project = _temp.CreateDirectory("unity");
        var library = _temp.CreateDirectory("unity", "Library");
        _temp.CreateDirectory("unity", "Library", "UnityLockfile");

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

        using var busy = StartWaiting(busyProject, new LiveTreeQuery(busyTarget, busyProject));

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

        // Waited on against the lookalike's own project, so the process is established before the
        // assertion that the real project is untouched by it.
        using var busy = StartWaiting(lookalike, new LiveTreeQuery(lookalike, lookalike));

        var findings = new LiveTreeInspector().FindLive([new LiveTreeQuery(target, project)]);

        Assert.False(findings.IsLive(target));
    }

    /// <summary>
    /// §6.3, and the answer here is a limit rather than a success. The Restart Manager refuses a path
    /// past <c>MAX_PATH</c> in both the plain and the extended-length form — established against a
    /// real file held open at 437 characters — so the lock-file signal genuinely cannot run that
    /// deep, and no amount of <c>LongPath</c> makes it.
    ///
    /// What this pins is that the failure is <em>reported</em>. A truncation or a refusal that read
    /// as "nothing holds this" would hand a live Unity project to a deletion, so the test requires
    /// the findings to come back incomplete: the plan then says the check could not run, which is the
    /// only honest thing to say. Asserting dormancy here would have passed whether or not any of this
    /// worked, which is what the test that used to sit here did.
    /// </summary>
    [Fact]
    public void ALockFilePastMaxPathIsReportedAsUncheckedRatherThanDormant()
    {
        var deep = Path.Combine(_temp.Path, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
        Directory.CreateDirectory(LongPath.Extended(deep));
        var library = Path.Combine(deep, "Library");
        Directory.CreateDirectory(LongPath.Extended(library));

        Assert.True(library.Length > 260, "the fixture is not long enough to test anything");

        var lockFile = Path.Combine(library, "UnityLockfile");
        File.WriteAllText(LongPath.Extended(lockFile), string.Empty);

        using var holder = File.Open(LongPath.Extended(lockFile), FileMode.Open, FileAccess.Read, FileShare.None);

        var findings = new LiveTreeInspector().FindLive(
            [new LiveTreeQuery(library, deep, ["UnityLockfile"])]);

        Assert.False(findings.Complete);
        Assert.False(findings.IsLive(library));
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

        using var busy = StartWaiting(project, new LiveTreeQuery(target, project));

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

    /// <param name="visibleAt">
    /// A query the started process must answer, which is what makes the wait a wait. A process that
    /// has begun but not finished starting has no environment block to read yet, so returning before
    /// the inspector can see it would leave every test here racing that initialisation. Waiting on
    /// the inspector itself is the only barrier that tests the thing the tests depend on.
    /// </param>
    private static WaitingProcess StartWaiting(
        string workingDirectory,
        LiveTreeQuery visibleAt,
        string? program = null)
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

        // Wrapped before anything can throw, so a failure while waiting still stops the child rather
        // than leaving a ping running for two minutes.
        var waiting = new WaitingProcess(Process.Start(start)!);

        Assert.True(
            SpinWait.SpinUntil(
                () => new LiveTreeInspector().FindLive([visibleAt]).IsLive(visibleAt.Directory),
                TimeSpan.FromSeconds(20)),
            "the helper process never became visible to the inspector, so the test below would prove nothing");

        return waiting;
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
