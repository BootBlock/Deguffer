using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the temporary-folder rows decided at Preview, asked again at Clean. Each rule an entry was
/// offered under — its tool not running, the tool's own word, no program working in the folder — can
/// be overturned by a program started while the preview is on screen.
///
/// <para>Each test plans with nothing running, starts something the way a user would between the two
/// presses, and then cleans. Everything runs against an invented folder through
/// <see cref="FakeUserEnvironment"/> and the process seams.</para>
/// </summary>
public sealed class TempMarkerCleanRecheckTests : IDisposable
{
    private const string Session = "0123456789abcdef0123456789abcdef";
    private const string OtherSession = "fedcba9876543210fedcba9876543210";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;
    private readonly FakeNamedMutexes _mutexes = FakeNamedMutexes.None;

    public TempMarkerCleanRecheckTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempToolCacheProvider CacheRow() =>
        new(_environment, new FakeProcessRunner(), _inspector, system: _system, liveTrees: _liveTrees, mutexes: _mutexes);

    private TempInstallerDownloadProvider InstallerRow() =>
        new(_environment, new FakeProcessRunner(), _inspector, system: _system, liveTrees: _liveTrees);

    private TempToolLogProvider LogRow() =>
        new(_environment, new FakeProcessRunner(), _inspector, system: _system, liveTrees: _liveTrees);

    /// <summary>The folder holding a file made by <see cref="Entry"/>.</summary>
    private string Folder(int bytes, params string[] segments) =>
        Path.GetDirectoryName(Entry(bytes, segments))!;

    private string Entry(int bytes, params string[] segments) => _temp.CreateFile(bytes, ["temp", .. segments]);

    /// <summary>
    /// Nothing ties a Flutter folder to its run, so the plan offered it because the Dart VM was not
    /// running. A run started before the clean may be using it, so it stays. Firefox's folder, held
    /// by other programs, still goes.
    /// </summary>
    [Fact]
    public async Task AToolStartedAfterThePreviewKeepsTheFoldersItHoldsBack()
    {
        var flutter = Folder(2048, "flutter_tools.1a2b3c", "app.dill");
        var firefox = Folder(1024, "mozilla-temp-files", "mozilla-temp-41");

        var provider = CacheRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(flutter, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(firefox, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _inspector.WithRunning("dart");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(flutter), "a Flutter folder was removed under a Dart VM started after the preview");
        Assert.False(Directory.Exists(firefox), "a folder no program started for was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: dart is running now.");
        AssertProvedStanding(result, flutter);
    }

    /// <summary>
    /// An updater download is offered only while its application is closed, because VS Code applies
    /// it from that folder when it restarts. VS Code opened before the clean keeps it; Docker's goes.
    /// </summary>
    [Fact]
    public async Task AnApplicationOpenedAfterThePreviewKeepsItsUpdateDownload()
    {
        var vsCode = Folder(4096, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe");
        var docker = Folder(2048, "DockerDesktopUpdates", "Docker Desktop Installer (223695).exe");

        var provider = InstallerRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(vsCode, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(docker, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _inspector.WithRunning("Code");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(vsCode), "VS Code's update was removed under a VS Code opened after the preview");
        Assert.False(Directory.Exists(docker), "an update whose application stayed closed was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: Code is running now.");
        AssertProvedStanding(result, vsCode);
    }

    /// <summary>
    /// Roslyn judges a session folder dead by the absence of the mutex named after it, and so does
    /// the plan. A mutex that exists by the clean is a session holding the folder, so the folder stays.
    /// The session whose mutex stayed gone is still removed.
    /// </summary>
    [Fact]
    public async Task ARoslynSessionWhoseMutexExistsByTheCleanSurvives()
    {
        var loader = Path.Combine(UserTemp, "Roslyn", "AnalyzerAssemblyLoader");
        var taken = Folder(4096, "Roslyn", "AnalyzerAssemblyLoader", Session, "1", "Analyzer.dll");
        var ended = Folder(2048, "Roslyn", "AnalyzerAssemblyLoader", OtherSession, "1", "Analyzer.dll");

        var provider = CacheRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(Path.Combine(loader, Session), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(loader, OtherSession), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _mutexes.WithExisting(Session);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(taken), "a session's analyzer copies were removed under its mutex");
        Assert.False(Directory.Exists(ended), "a session whose mutex stayed gone was kept");
        Assert.Contains(
            result.Steps,
            step => step.Message == "Nothing was removed: an editing session is still using these analyzer copies.");
        AssertProvedStanding(result, Path.Combine(loader, Session));
    }

    /// <summary>
    /// Every recognised folder is offered only while no program is running from it or working in it,
    /// whatever its tool. One started from inside Node's cache before the clean keeps the cache, and
    /// the Flutter folder beside it still goes.
    /// </summary>
    [Fact]
    public async Task AProgramStartedInAFolderAfterThePreviewKeepsIt()
    {
        var cache = Path.Combine(UserTemp, "node-compile-cache");
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        var flutter = Folder(2048, "flutter_tools.1a2b3c", "app.dill");

        var provider = CacheRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _liveTrees.WithProgram("node", executable: Path.Combine(cache, "node.exe"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(cache), "a folder a program started from after the preview was removed");
        Assert.False(Directory.Exists(flutter), "a folder no program is in was kept");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: node is running from inside it.");
        AssertProvedStanding(result, cache);
    }

    /// <summary>
    /// The survey offers no folder on a process table it could not read, so one that cannot be read
    /// by the clean holds every folder back. A log file was never asked about a working directory, and
    /// still goes.
    /// </summary>
    [Fact]
    public async Task AProcessTableUnreadableAtTheCleanHoldsFoldersBackButNotFiles()
    {
        var trace = Entry(4096, "DiagOutputDir", "RdClientAutoTrace", "RdClientAutoTrace-WppAutoTrace-20260901.etl");
        var traces = Path.GetDirectoryName(trace)!;
        var updaterLog = Entry(512, "vscode-inno-updater-1756000000.log");

        var provider = LogRow();
        var plan = await provider.PlanAsync();

        Assert.Contains(traces, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(updaterLog, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        _liveTrees.CannotTellFromNow();

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(trace), "a log folder was emptied on a process table nobody could read");
        Assert.False(File.Exists(updaterLog), "a log file no working directory speaks for was kept");
        Assert.Contains(
            result.Steps,
            step => step.Message == "Nothing was removed: Deguffer could not tell whether a running program is using this.");
        AssertProvedStanding(result, traces);
    }

    /// <summary>
    /// Every check §5.6 made of <paramref name="path"/> found it standing, and there is at least one.
    /// A folder emptied in place is protected by the plan already, so the run may name it twice.
    /// </summary>
    private static void AssertProvedStanding(CleanupResult result, string path)
    {
        var checks = result.Verification!.Checks
            .Where(c => c.Subject.Equals(path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.Equal(VerificationOutcome.Survived, check.Outcome));
        Assert.True(result.Verification.Passed, result.Verification.Summary);
    }
}
