using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the live-tree veto decided at Preview, asked again at Clean, for every row whose offer rests
/// on it: the build directories in approved source folders, .NET's <c>obj</c>, Squirrel's staging
/// folder and the builds a Squirrel application replaced.
///
/// <para>Each test plans with nothing running, starts a program the way a user would between the two
/// presses, and then cleans. The directory the program took up is left standing, the step says why,
/// §5.6 proves it survived, and the one beside it that nothing took up is still removed. Everything
/// runs against an invented tree through <see cref="FakeUserEnvironment"/> and
/// <see cref="FakeLiveTreeInspector"/>.</para>
/// </summary>
public sealed class LiveTreeCleanRecheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;

    public LiveTreeCleanRecheckTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    public enum Toolchain
    {
        Unity,
        Cargo,
        Node,
        Python,
        UnrealIntermediate,
        UnrealDerivedData,
    }

    /// <summary>
    /// The case the question exists for. A project nothing was using at the preview is opened in an
    /// editor, or a build is started in it, before the clean. Its build directory stays, and the idle
    /// project's still goes.
    /// </summary>
    [Theory]
    [InlineData(Toolchain.Unity)]
    [InlineData(Toolchain.Cargo)]
    [InlineData(Toolchain.Node)]
    [InlineData(Toolchain.Python)]
    [InlineData(Toolchain.UnrealIntermediate)]
    [InlineData(Toolchain.UnrealDerivedData)]
    public async Task ABuildDirectoryWhoseProjectIsOpenedAfterThePreviewSurvivesTheClean(Toolchain toolchain)
    {
        var root = ApproveRoot();
        var busyProject = Path.Combine(root, "Busy");
        var busy = CreateRecognised(toolchain, busyProject);
        var idle = CreateRecognised(toolchain, Path.Combine(root, "Idle"));

        var provider = ProviderFor(toolchain);
        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("editor", workingDirectory: busyProject);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "a build directory whose project was opened after the preview was removed");
        Assert.False(Directory.Exists(idle), "a build directory nothing took up was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: editor is working in Busy.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// A preview that read every program, followed by a clean that cannot: the process table, or the
    /// Restart Manager asked about a lock file, does not answer. The directory was offered on an answer
    /// the clean no longer has, so it stays, and the step says why.
    /// </summary>
    [Theory]
    [InlineData(Toolchain.Unity)]
    [InlineData(Toolchain.Cargo)]
    [InlineData(Toolchain.Node)]
    [InlineData(Toolchain.Python)]
    [InlineData(Toolchain.UnrealIntermediate)]
    [InlineData(Toolchain.UnrealDerivedData)]
    public async Task ABuildDirectoryIsLeftAloneWhenTheCleanCannotReadWhatThePreviewRead(Toolchain toolchain)
    {
        var root = ApproveRoot();
        var directory = CreateRecognised(toolchain, Path.Combine(root, "Game"));

        var provider = ProviderFor(toolchain);
        var plan = await provider.PlanAsync();

        AssertOffered(plan, directory);

        _liveTrees.CannotTellFromNow();

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(directory), "a build directory was removed on an answer the preview never gave");
        Assert.Contains(
            result.Steps,
            step => step.Message == "Nothing was removed: Deguffer could not tell whether a running program is using this.");
        AssertProvedStanding(result, directory);
    }

    /// <summary>
    /// A plan made on a partial answer offered the directory and said so in a note. The same answer at
    /// the clean is no new reason to refuse it, and the project around it survives.
    /// </summary>
    [Fact]
    public async Task ABuildDirectoryOfferedOnAPartialAnswerIsRemovedOnTheSameAnswer()
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "Game");
        var directory = CreateRecognised(Toolchain.Unity, project);

        _liveTrees.CannotTellFromNow();

        var provider = ProviderFor(Toolchain.Unity);
        var plan = await provider.PlanAsync();

        AssertOffered(plan, directory);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(directory), "a build directory the preview offered on a partial answer was kept on the same answer");
        Assert.True(Directory.Exists(project), "the project around a removed build directory was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The veto's other evidence, read afresh: a program started from inside the build directory after
    /// the preview, such as a binary run out of <c>target\debug</c> or an environment's interpreter.
    /// </summary>
    [Theory]
    [InlineData(Toolchain.Cargo)]
    [InlineData(Toolchain.Python)]
    public async Task ABuildDirectoryAProgramStartsRunningFromAfterThePreviewSurvivesTheClean(Toolchain toolchain)
    {
        var root = ApproveRoot();
        var busy = CreateRecognised(toolchain, Path.Combine(root, "Busy"));
        var idle = CreateRecognised(toolchain, Path.Combine(root, "Idle"));

        var provider = ProviderFor(toolchain);
        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("app", Path.Combine(busy, "app.exe"), _temp.Path);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "a build directory a program started running from was removed");
        Assert.False(Directory.Exists(idle), "a build directory nothing took up was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: app is running from inside it.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// The Unreal editor works in the engine's folder, so what finds it is its log in the project's
    /// <c>Saved\Logs</c>, held open for the whole session. A project nobody had opened has no log at
    /// the preview. The clean asks about the logs there are now, by the veto's own rule, so the log an
    /// editor opened afterwards writes is the one that holds the directory back.
    /// </summary>
    [Theory]
    [InlineData(Toolchain.UnrealIntermediate)]
    [InlineData(Toolchain.UnrealDerivedData)]
    public async Task AnUnrealProjectWhoseEditorWritesItsFirstLogAfterThePreviewKeepsItsBuildDirectory(Toolchain toolchain)
    {
        var root = ApproveRoot();
        var busyProject = Path.Combine(root, "Busy");
        var busy = CreateRecognised(toolchain, busyProject);
        var idle = CreateRecognised(toolchain, Path.Combine(root, "Idle"));

        var provider = ProviderFor(toolchain);
        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        var log = Path.Combine(busyProject, "Saved", "Logs", "Game.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "LogInit: Display: Running engine for game: Game");
        _liveTrees.WithHeldFile(log, "UnrealEditor");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "the build directory of a project an editor opened after the preview was removed");
        Assert.False(Directory.Exists(idle), "a build directory nothing took up was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: UnrealEditor has it open.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// The same question for <c>obj</c>, where it carries the most force: a build started after the
    /// preview is writing into the directory the clean would remove.
    /// </summary>
    [Fact]
    public async Task AnObjWhoseProjectABuildStartsInAfterThePreviewSurvivesTheClean()
    {
        var root = ApproveRoot();
        var busyProject = Path.Combine(root, "Busy");
        var busy = ProjectFixture.CreateProject(busyProject, "Busy");
        var idle = ProjectFixture.CreateProject(Path.Combine(root, "Idle"), "Idle");

        var scanner = new FakeDirectoryScanner();
        var provider = new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            _liveTrees,
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);

        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("MSBuild", workingDirectory: busyProject);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "an obj a build started in after the preview was removed");
        Assert.False(Directory.Exists(idle), "an obj nothing took up was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: MSBuild is working in Busy.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// The same for Visual Studio opened after the preview on a solution kept above its projects. The
    /// solution's folder is read again at the clean, because the editor is in it only now.
    /// </summary>
    [Fact]
    public async Task AnObjWhoseSolutionIsOpenedAfterThePreviewSurvivesTheClean()
    {
        var root = ApproveRoot();
        var solution = Path.Combine(root, "Solution");
        var busy = ProjectFixture.CreateProject(Path.Combine(solution, "Busy"), "Busy");
        var idle = ProjectFixture.CreateProject(Path.Combine(solution, "Idle"), "Idle");
        ProjectFixture.CreateSolution(solution, "Solution", xml: false, Path.Combine(solution, "Busy", "Busy.csproj"));

        var scanner = new FakeDirectoryScanner();
        var provider = new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            new SnapshottingLiveTrees(_liveTrees),
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);

        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("devenv", workingDirectory: solution);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "an obj whose solution was opened after the preview was removed");
        Assert.False(Directory.Exists(idle), "an obj the solution does not name was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: devenv is working in Solution.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// The check drops the preview's reading of the machine before it reads where programs are. The
    /// inspector here keeps that reading until invalidated, as the real one keeps its process table,
    /// and the plan has one step, so the first check of the clean is the one that decides: an earlier
    /// step's check would otherwise have dropped the reading for it.
    /// </summary>
    [Fact]
    public async Task TheCheckAtTheCleanReadsWhereProgramsAreAfterDroppingThePreviewsReading()
    {
        var root = ApproveRoot();
        var solution = Path.Combine(root, "Solution");
        var busy = ProjectFixture.CreateProject(Path.Combine(solution, "Busy"), "Busy");
        ProjectFixture.CreateSolution(solution, "Solution", xml: false, Path.Combine(solution, "Busy", "Busy.csproj"));

        var scanner = new FakeDirectoryScanner();
        var provider = new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            new SnapshottingLiveTrees(_liveTrees),
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);

        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy);

        _liveTrees.WithProgram("devenv", workingDirectory: solution);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "the check read the preview's machine, where nothing had the solution open");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// A project added to a solution that was already open at the preview. The preview read the
    /// solution, because the editor was in its folder, and found only the other project in it. The
    /// clean reads it again, so the project added since keeps its <c>obj</c>.
    /// </summary>
    [Fact]
    public async Task AnObjWhoseProjectIsAddedToAnOpenSolutionAfterThePreviewSurvivesTheClean()
    {
        var root = ApproveRoot();
        var solution = Path.Combine(root, "Solution");
        var open = ProjectFixture.CreateProject(Path.Combine(solution, "Open"), "Open");
        var added = ProjectFixture.CreateProject(Path.Combine(solution, "Added"), "Added");
        var openProject = Path.Combine(solution, "Open", "Open.csproj");
        ProjectFixture.CreateSolution(solution, "Solution", xml: false, openProject);

        _liveTrees.WithProgram("devenv", workingDirectory: solution);

        var scanner = new FakeDirectoryScanner();
        var provider = new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory()),
            new SnapshottingLiveTrees(_liveTrees),
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner);

        var plan = await provider.PlanAsync();

        // The premise: the solution was read at the preview, and named only the open project.
        Assert.Equal([added], plan.TargetedPaths);

        ProjectFixture.CreateSolution(
            solution, "Solution", xml: false, openProject, Path.Combine(solution, "Added", "Added.csproj"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(added), "an obj whose project was added to the open solution after the preview was removed");
        Assert.True(Directory.Exists(open), "an obj the plan held back was removed");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: devenv is working in Solution.");
        AssertProvedStanding(result, added);
    }

    /// <summary>
    /// Every Squirrel application shares the staging folder, which is why the plan refuses a directory
    /// an install is running from. One that starts running from a directory the preview offered keeps
    /// it, and the other leftover still goes.
    /// </summary>
    [Fact]
    public async Task AStagingDirectoryAnInstallStartsRunningFromAfterThePreviewSurvivesTheClean()
    {
        var staging = Path.Combine(_environment.LocalAppData, SquirrelDiscovery.StagingDirectoryName);
        var busy = Populate(Path.Combine(staging, "tempa"));
        var idle = Populate(Path.Combine(staging, "tempb"));

        var provider = new SquirrelStagingProvider(
            _environment,
            liveTrees: _liveTrees,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("Setup", Path.Combine(busy, "Setup.exe"), _temp.Path);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "a staging directory an install started running from was removed");
        Assert.False(Directory.Exists(idle), "a staging directory nothing took up was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: Setup is running from inside it.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// The question is asked about the installation, because an application runs from the build that
    /// replaced the ones offered. One started after the preview keeps its old build, named at the
    /// build's own path, and another application's old build still goes.
    /// </summary>
    [Fact]
    public async Task ASupersededBuildOfAnApplicationStartedAfterThePreviewSurvivesTheClean()
    {
        var running = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var closed = CreateApplication("Notebook", "1.0.0", "1.1.0");
        var busy = Path.Combine(running, "app-3.6.3");
        var idle = Path.Combine(closed, "app-1.0.0");

        var provider = new SquirrelSupersededVersionProvider(
            _environment,
            liveTrees: _liveTrees,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning);

        var plan = await provider.PlanAsync();

        AssertOffered(plan, busy, idle);

        _liveTrees.WithProgram("Chatterbox", Path.Combine(running, "app-3.6.4", "Chatterbox.exe"), _temp.Path);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(busy), "the old build of an application started after the preview was removed");
        Assert.False(Directory.Exists(idle), "the old build of an application that stayed closed was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: Chatterbox is running from inside it.");
        AssertProvedStanding(result, busy);
    }

    /// <summary>
    /// <see cref="FakeLiveTreeInspector"/> with the one property of the real inspector it lacks: where
    /// programs are is read once and kept until <see cref="Invalidate"/>, as the real inspector keeps
    /// its process table. A check that read before invalidating would see the preview's machine.
    /// </summary>
    private sealed class SnapshottingLiveTrees(FakeLiveTreeInspector live) : ILiveTreeInspector
    {
        private LiveTreeFindings? _occupied;

        public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default) =>
            live.FindLive(candidates, ct);

        public LiveTreeFindings FindOccupiedDirectories(CancellationToken ct = default) =>
            _occupied ??= live.FindOccupiedDirectories(ct);

        public LiveTreeFindings FindLiveChildren(IReadOnlyList<string> directories, CancellationToken ct = default) =>
            live.FindLiveChildren(directories, ct);

        public void Invalidate()
        {
            _occupied = null;
            live.Invalidate();
        }
    }

    private static void AssertOffered(CleanupPlan plan, params string[] paths)
    {
        foreach (var path in paths)
        {
            Assert.Contains(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var check = Assert.Single(result.Verification!.Checks, c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VerificationOutcome.Survived, check.Outcome);
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }

    private string ApproveRoot()
    {
        var root = _temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    /// <summary>A directory with a file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    /// <summary>A Squirrel application: the updater beside it, its packages, and a folder per build.</summary>
    private string CreateApplication(string name, params string[] versions)
    {
        var root = Path.Combine(_environment.LocalAppData, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        Populate(Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName));

        foreach (var version in versions)
        {
            Populate(Path.Combine(root, "app-" + version));
        }

        return root;
    }

    private static string CreateRecognised(Toolchain toolchain, string project) => toolchain switch
    {
        Toolchain.Unity => BuildDirectoryFixture.CreateUnityProject(project),
        Toolchain.Cargo => BuildDirectoryFixture.CreateCargoProject(project),
        Toolchain.Node => BuildDirectoryFixture.CreateNodeProject(project),
        Toolchain.Python => BuildDirectoryFixture.CreatePythonProject(project),
        Toolchain.UnrealIntermediate => BuildDirectoryFixture.CreateUnrealProject(project),
        Toolchain.UnrealDerivedData => BuildDirectoryFixture.CreateUnrealProject(project, directoryName: "DerivedDataCache"),
        _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null),
    };

    private BuildDirectoryProvider ProviderFor(Toolchain toolchain)
    {
        var scanner = new FakeDirectoryScanner();
        var discovery = new SourceDirectoryDiscovery(scanner, new FakeVolumeInventory());
        var runner = new FakeProcessRunner();
        var inspector = FakeProcessInspector.NothingRunning;

        return toolchain switch
        {
            Toolchain.Unity => new UnityLibraryProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            Toolchain.Cargo => new CargoTargetProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            Toolchain.Node => new NodeModulesProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            Toolchain.Python => new PythonVirtualEnvironmentProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            Toolchain.UnrealIntermediate => new UnrealIntermediateProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            Toolchain.UnrealDerivedData => new UnrealProjectDerivedDataProvider(_roots, discovery, _liveTrees, _environment, runner, inspector, scanner),
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null),
        };
    }
}
