using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Toolchain = Deguffer.Core.Tests.BuildDirectoryProviderTests.Toolchain;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1 over the build directories a plan holds back with no program near them. Explore found its
/// candidates from the places programs are, so a directory the plan withheld for another reason (the
/// process table unreadable, a lock file that could not be asked about, or one a program holds open
/// while it works elsewhere) was never asked about, and Explore allowed what the Storage page withheld.
///
/// <para>Each test first shows the plan withholding the directory, then that Explore refuses it, and
/// then that what the plan neither offers nor withholds stays allowed, so the refusal is the plan's
/// verdict and not a refusal of everything in the folder.</para>
/// </summary>
public sealed class ExploreWithheldBuildDirectoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;
    private readonly FakeVolumeInventory _volumes = new();

    public ExploreWithheldBuildDirectoryTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The process table unreadable, and no program anywhere near the project. The plan holds the
    /// directory back as unchecked. The same machine with the table read allows it, which is the
    /// premise that the refusal is the withholding.
    /// </summary>
    [Theory]
    [InlineData(Toolchain.Unity)]
    [InlineData(Toolchain.Cargo)]
    [InlineData(Toolchain.Node)]
    [InlineData(Toolchain.Python)]
    [InlineData(Toolchain.Unreal)]
    public async Task ExploreRefusesWhatThePlanWithholdsWhenTheProcessTableCannotBeRead(Toolchain toolchain)
    {
        var root = ApproveRootInProfile();
        var project = Path.Combine(root, "Quiet");
        var withheld = CreateRecognised(toolchain, project);
        var lookalike = CreateUnrecognised(toolchain, Path.Combine(root, "Lookalike"));
        var notes = Directory.CreateDirectory(Path.Combine(root, "Notes")).FullName;

        Assert.True((await PolicyAsync(Provider(toolchain, FakeLiveTreeInspector.NothingLive))).MayRemove(withheld).IsAllowed);

        var provider = Provider(toolchain, FakeLiveTreeInspector.CannotTell);
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == withheld && p.Reason == LiveTreeVeto.UncheckedReason);

        var policy = await PolicyAsync(provider);
        var refusal = policy.MayRemove(withheld);

        Assert.False(refusal.IsAllowed);
        Assert.Contains("could not tell whether a running program is using this", refusal.Reason, StringComparison.Ordinal);
        Assert.False(policy.MayRemove(Directory.EnumerateFileSystemEntries(withheld).First()).IsAllowed);
        Assert.False(policy.MayRemove(project).IsAllowed);

        // Never offered by the plan, which does not recognise it, so not refused here either.
        Assert.True(policy.MayRemove(lookalike).IsAllowed);
        Assert.True(policy.MayRemove(notes).IsAllowed);
    }

    /// <summary>The same for a .NET <c>obj</c>, whose provider asks the veto a different question.</summary>
    [Fact]
    public async Task ExploreRefusesAnObjThePlanWithholdsWhenTheProcessTableCannotBeRead()
    {
        var root = ApproveRootInProfile();
        var project = Path.Combine(root, "Quiet");
        var obj = ProjectFixture.CreateProject(project, "Quiet");
        var source = Path.Combine(project, "Program.cs");
        File.WriteAllText(source, "// the user's own source");

        Assert.True((await PolicyAsync(DotNetObj(FakeLiveTreeInspector.NothingLive))).MayRemove(obj).IsAllowed);

        var provider = DotNetObj(FakeLiveTreeInspector.CannotTell);
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == obj && p.Reason == LiveTreeVeto.UncheckedReason);

        var policy = await PolicyAsync(provider);

        Assert.False(policy.MayRemove(obj).IsAllowed);
        Assert.False(policy.MayRemove(project).IsAllowed);
        Assert.True(policy.MayRemove(source).IsAllowed);
    }

    /// <summary>
    /// A solution that could not be read may name any project below it, so a program working beside
    /// it makes the plan hold back every one of them. Walking up from where the program works never
    /// reaches a project below the solution, and nothing names one.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAnObjOnlyAnUnreadableSolutionCouldName()
    {
        var root = ApproveRootInProfile();
        var solution = Path.Combine(root, "Solution");
        var first = ProjectFixture.CreateProject(Path.Combine(solution, "ProjectA"), "ProjectA");
        var second = ProjectFixture.CreateProject(Path.Combine(solution, "Nested", "ProjectB"), "ProjectB");
        var outside = ProjectFixture.CreateProject(Path.Combine(root, "Other"), "Other");
        File.WriteAllText(Path.Combine(solution, "Solution.sln"), "not a solution");

        var provider = DotNetObj(new FakeLiveTreeInspector().WithProgram("devenv", workingDirectory: solution));
        var plan = await provider.PlanAsync();

        Assert.Equal([outside], plan.TargetedPaths);

        var policy = await PolicyAsync(provider);

        Assert.False(policy.MayRemove(first).IsAllowed);
        Assert.False(policy.MayRemove(second).IsAllowed);
        Assert.True(policy.MayRemove(outside).IsAllowed);
    }

    /// <summary>
    /// The table read in full, and the question about one project's lock file alone not asked, as for
    /// a Unity project past <c>MAX_PATH</c>. No program is near it, so nothing made it a candidate.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesALibraryWhoseLockFileCouldNotBeAskedAboutWithNoProgramNearIt()
    {
        var root = ApproveRootInProfile();
        var withheld = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Long"));
        var other = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Other"));

        var provider = Provider(Toolchain.Unity, new FakeLiveTreeInspector().WithUnanswered(withheld));
        var plan = await provider.PlanAsync();

        Assert.Equal([other], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == withheld && p.Reason == LiveTreeVeto.UncheckedReason);

        var policy = await PolicyAsync(provider);

        Assert.False(policy.MayRemove(withheld).IsAllowed);
        Assert.True(policy.MayRemove(other).IsAllowed);
    }

    /// <summary>
    /// A Unity editor holding the project's lock file while its working directory is elsewhere. The
    /// plan's question finds it, and walking up from where the editor works never reaches the project.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesALibraryWhoseLockFileAProgramWorkingElsewhereHolds()
    {
        var root = ApproveRootInProfile();
        var held = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Open"));
        var other = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Closed"));

        var live = new FakeLiveTreeInspector()
            .WithProgram("Unity", Path.Combine(_temp.Path, "Editor", "Unity.exe"), _temp.Path)
            .WithHeldFile(Path.Combine(held, "UnityLockfile"), "Unity");

        var provider = Provider(Toolchain.Unity, live);
        var plan = await provider.PlanAsync();

        Assert.Equal([other], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == held);

        var policy = await PolicyAsync(provider);
        var refusal = policy.MayRemove(held);

        Assert.False(refusal.IsAllowed);
        Assert.Contains("Unity has it open", refusal.Reason, StringComparison.Ordinal);
        Assert.True(policy.MayRemove(other).IsAllowed);
    }

    /// <summary>
    /// The policy a scan builds reads the scan's tree rather than walking the root again: a project
    /// created after the scan is not in what it declares, and is in what a search of the disk declares.
    /// </summary>
    [Fact]
    public async Task AScanPolicyFindsWhatThePlanWithholdsInTheScansTree()
    {
        var root = ApproveRootInProfile();
        var listed = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Listed"));
        var scope = Scanned(root);
        var later = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Later"));

        var fromTree = await Provider(Toolchain.Unity, FakeLiveTreeInspector.CannotTell, scope).DiscoverToolRootsAsync();
        var fromDisk = await Provider(Toolchain.Unity, FakeLiveTreeInspector.CannotTell).DiscoverToolRootsAsync();

        Assert.Equal([listed], fromTree.Select(declared => declared.Path));
        Assert.Equal([later, listed], fromDisk.Select(declared => declared.Path).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An approved root the scan does not reach is not searched, and is refused whole: the scope said
    /// nothing it covers is there, so the refusal takes nothing a caller meant to ask about, and it is
    /// what holds if one asks anyway.
    /// </summary>
    [Fact]
    public async Task ARootTheScanDoesNotReachIsRefusedWhole()
    {
        var root = ApproveRootInProfile();
        var library = BuildDirectoryFixture.CreateUnityProject(Path.Combine(root, "Quiet"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(_environment.UserProfile, "Music")).FullName;

        var provider = Provider(Toolchain.Unity, FakeLiveTreeInspector.NothingLive, Scanned(elsewhere));
        var declared = Assert.Single(await provider.DiscoverToolRootsAsync());

        Assert.Equal(root, declared.Path);
        Assert.StartsWith("Deguffer did not look in here", declared.Reason, StringComparison.Ordinal);

        var policy = await PolicyAsync(provider);

        Assert.False(policy.MayRemove(library).IsAllowed);
        Assert.True(policy.MayRemove(elsewhere).IsAllowed);
    }

    /// <summary>
    /// The candidates a scan's tree gives are the ones the disk gives, by the same boundary: not inside
    /// <c>.git</c>, not inside another candidate, and nothing past a refused listing. The paths are in
    /// display form, as the walk spells them (§6.3).
    /// </summary>
    [Fact]
    public async Task AScannedRootGivesTheCandidatesTheDiskGives()
    {
        var root = ApproveRootInProfile();
        Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "App", "src", "Lib", "obj", "nested", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "Web", "node_modules", "pkg", "obj"));
        Directory.CreateDirectory(Path.Combine(root, ".git", "objects", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        var locked = Directory.CreateDirectory(Path.Combine(root, "Locked")).FullName;
        Directory.CreateDirectory(Path.Combine(locked, "Hidden", "obj"));

        SourceDiscovery fromTree, fromDisk;

        using (new DeniedDirectory(locked))
        {
            fromTree = await Discovery(Scanned(root)).FindAsync(_roots.Load());
            fromDisk = await Discovery().FindAsync(_roots.Load());
        }

        Assert.Equal(
            [Path.Combine(root, "App", "obj"), Path.Combine(root, "App", "src", "Lib", "obj"), Path.Combine(root, "Web", "node_modules")],
            fromDisk.Candidates.Order(StringComparer.Ordinal));
        Assert.Equal(fromDisk.Candidates.Order(StringComparer.Ordinal), fromTree.Candidates.Order(StringComparer.Ordinal));
        Assert.Equal([locked], fromDisk.UnreadableDirectories);
        Assert.Equal(fromDisk.UnreadableDirectories, fromTree.UnreadableDirectories);
        Assert.All(fromTree.Candidates, candidate => Assert.False(candidate.StartsWith(@"\\?\", StringComparison.Ordinal)));
        Assert.Empty(fromTree.NotSearchedRoots);
    }

    /// <summary>
    /// It is the tree being read and not the disk: a directory created after the scan is not in what
    /// the scan found. Once the discovery is told the disk has changed, the disk is searched again.
    /// </summary>
    [Fact]
    public async Task AScannedRootIsReadFromTheTreeUntilTheDiscoveryIsInvalidated()
    {
        var root = ApproveRootInProfile();
        Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
        var discovery = Discovery(Scanned(root));
        var later = Directory.CreateDirectory(Path.Combine(root, "Later", "obj")).FullName;

        Assert.DoesNotContain(later, (await discovery.FindAsync(_roots.Load())).Candidates);

        discovery.Invalidate();

        Assert.Contains(later, (await discovery.FindAsync(_roots.Load())).Candidates);
    }

    /// <summary>
    /// A tree drawn from a file table that was not read whole may lack any record, so it never stands
    /// in for the disk.
    /// </summary>
    [Fact]
    public async Task AnIncompleteFileTableIsNotReadInPlaceOfTheDisk()
    {
        var root = ApproveRootInProfile();
        var scan = ExploreScan.Fast(Scan(root), fromIncompleteTable: true);
        var later = Directory.CreateDirectory(Path.Combine(root, "Later", "obj")).FullName;

        var discovery = Discovery(ProtectionScope.Scanned(root, Task.FromResult<ExploreScan?>(scan), _volumes));

        Assert.Contains(later, (await discovery.FindAsync(_roots.Load())).Candidates);
    }

    /// <summary>
    /// An approved root inside the profile. The scratch tree's default sits beside the profile, where
    /// the region table refuses everything as another account's.
    /// </summary>
    private string ApproveRootInProfile()
    {
        var root = Path.Combine(_environment.UserProfile, "source");
        Directory.CreateDirectory(root);
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private static ExploreTree Scan(string root) =>
        WalkExploreReader.Read(root, ScanTuner.Shipped, OccupancyProbe.Default, progress: null, TimeProvider.System, default);

    private ProtectionScope Scanned(string root) =>
        ProtectionScope.Scanned(root, Task.FromResult<ExploreScan?>(ExploreScan.Walked(Scan(root), FallbackReason.NotElevated)), _volumes);

    private SourceDirectoryDiscovery Discovery(ProtectionScope? scope = null)
    {
        var discovery = new SourceDirectoryDiscovery(new FakeDirectoryScanner(), _volumes, scope: scope);
        discovery.Include(["obj", "node_modules"]);
        return discovery;
    }

    private Task<ExploreActionPolicy> PolicyAsync(ICleanupProvider provider) =>
        ExploreActionPolicy.ForAsync(new FakeSystemDirectories(_temp.Path), _environment, _volumes, [provider]);

    private BuildDirectoryProvider Provider(Toolchain toolchain, ILiveTreeInspector live, ProtectionScope? scope = null)
    {
        var scanner = new FakeDirectoryScanner();
        var discovery = new SourceDirectoryDiscovery(scanner, _volumes, scope: scope);

        return toolchain switch
        {
            Toolchain.Unity => new UnityLibraryProvider(
                _roots, discovery, live, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner),
            Toolchain.Cargo => new CargoTargetProvider(
                _roots, discovery, live, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner),
            Toolchain.Node => new NodeModulesProvider(
                _roots, discovery, live, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner),
            Toolchain.Python => new PythonVirtualEnvironmentProvider(
                _roots, discovery, live, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner),
            Toolchain.Unreal => new UnrealIntermediateProvider(
                _roots, discovery, live, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner),
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null),
        };
    }

    private DotNetObjProvider DotNetObj(ILiveTreeInspector live)
    {
        var scanner = new FakeDirectoryScanner();

        return new DotNetObjProvider(
            _roots,
            new SourceDirectoryDiscovery(scanner, _volumes),
            live,
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            scanner,
            _volumes);
    }

    private static string CreateRecognised(Toolchain toolchain, string project) => toolchain switch
    {
        Toolchain.Unity => BuildDirectoryFixture.CreateUnityProject(project),
        Toolchain.Cargo => BuildDirectoryFixture.CreateCargoProject(project),
        Toolchain.Node => BuildDirectoryFixture.CreateNodeProject(project),
        Toolchain.Python => BuildDirectoryFixture.CreatePythonProject(project),
        Toolchain.Unreal => BuildDirectoryFixture.CreateUnrealProject(project),
        _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null),
    };

    private static string CreateUnrecognised(Toolchain toolchain, string project) => toolchain switch
    {
        Toolchain.Unity => BuildDirectoryFixture.CreateUnityProject(project, writePackages: false),
        Toolchain.Cargo => BuildDirectoryFixture.CreateCargoProject(project, writeCacheTag: false),
        Toolchain.Node => BuildDirectoryFixture.CreateNodeProject(project, lockFile: null),
        Toolchain.Python => BuildDirectoryFixture.CreatePythonProject(project, writeConfig: false),
        Toolchain.Unreal => BuildDirectoryFixture.CreateUnrealProject(project, descriptor: null),
        _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null),
    };
}
