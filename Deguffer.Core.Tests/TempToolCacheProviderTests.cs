using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.2 in a folder that belongs to nobody: what a tool's own name identifies may go, each under the
/// live check that tool allows, and nothing unrecognised is touched.
/// </summary>
public sealed class TempToolCacheProviderTests : IDisposable
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public TempToolCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private TempToolCacheProvider CreateProvider(
        IProcessInspector? inspector = null,
        ILiveTreeInspector? liveTrees = null,
        INamedMutexes? mutexes = null,
        IVolumeInventory? volumes = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive,
            mutexes: mutexes ?? FakeNamedMutexes.None,
            volumes: volumes ?? new FakeVolumeInventory());

    private string Entry(int bytes, params string[] segments) => _temp.CreateFile(bytes, ["temp", .. segments]);

    [Fact]
    public async Task TakesWhatAToolsOwnNameIdentifiesAndNothingBesideIt()
    {
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        Entry(2048, "flutter_tools.1a2b3c", "app.dill");
        Entry(1024, "mozilla-temp-files", "mozilla-temp-41");
        var stranger = Entry(8192, "q4mzt0xk", "payload.cab");
        var lookalike = Entry(512, "flutter_tools_chrome_device.1", "Preferences");
        var loose = Entry(256, "node-compile-cache.txt");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[]
            {
                Path.Combine(UserTemp, "flutter_tools.1a2b3c"),
                Path.Combine(UserTemp, "mozilla-temp-files"),
                Path.Combine(UserTemp, "node-compile-cache"),
            }.Order(StringComparer.Ordinal),
            plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path).Order(StringComparer.Ordinal));
        Assert.Equal(4096 + 2048 + 1024, plan.EstimatedBytes);
        Assert.Contains(plan.Steps.OfType<DeleteStep>(), s => s.Group == "Node.js compile cache");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(UserTemp), "the temporary folder itself was removed");
        Assert.True(File.Exists(stranger), "an unrecognised folder in the temporary folder was removed");
        Assert.True(File.Exists(lookalike), "a name that only resembles a marker was removed");
        Assert.True(File.Exists(loose), "a file named like a directory marker was removed");
        Assert.False(Directory.Exists(Path.Combine(UserTemp, "node-compile-cache")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Roslyn's own test for a stale session is its mutex, so a session whose mutex exists stays
    /// whatever its age, and the folders Roslyn owns survive with whatever they hold besides sessions.
    /// </summary>
    [Fact]
    public async Task TakesOnlyTheRoslynSessionsWhoseMutexHasGone()
    {
        const string live = "fedcba9876543210fedcba9876543210";
        var loader = Path.Combine(UserTemp, "Roslyn", "AnalyzerAssemblyLoader");

        Entry(4096, "Roslyn", "AnalyzerAssemblyLoader", Session, "1", "Analyzer.dll");
        Entry(2048, "Roslyn", "AnalyzerAssemblyLoader", live, "1", "Analyzer.dll");
        var unrecognised = Entry(512, "Roslyn", "AnalyzerAssemblyLoader", "notes", "readme.txt");
        var sharedCache = Entry(256, "Roslyn", "AnalyzerPathResolver", "v1", "cache", "Analyzer.dll");
        Entry(1024, "Roslyn", "AnalyzerPathResolver", "v1", "shadow", Session.Replace('0', 'a'), "Analyzer.dll");

        var provider = CreateProvider(mutexes: new FakeNamedMutexes(live));
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[]
            {
                Path.Combine(UserTemp, "Roslyn", "AnalyzerAssemblyLoader", Session),
                Path.Combine(UserTemp, "Roslyn", "AnalyzerPathResolver", "v1", "shadow", Session.Replace('0', 'a')),
            }.Order(StringComparer.Ordinal),
            plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path).Order(StringComparer.Ordinal));

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(loader, live), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(loader, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(loader, "notes"), StringComparison.OrdinalIgnoreCase) && p.HeldContentBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Path.Combine(loader, live)), "a session Roslyn still holds was removed");
        Assert.True(File.Exists(unrecognised), "something Roslyn's folder holds besides sessions was removed");
        Assert.True(File.Exists(sharedCache), "Roslyn's shared analyzer cache was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Nothing ties a Flutter folder to the run using it, so all of them wait for the Dart VM to
    /// exit — and the row says which program to close.
    /// </summary>
    [Fact]
    public async Task HoldsBackEveryFlutterFolderWhileTheDartVmRuns()
    {
        var run = Path.GetDirectoryName(Entry(2048, "flutter_tools.ff", "app.dill"))!;

        var plan = await CreateProvider(new FakeProcessInspector("dart")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined, "a row holding something back read as already clear");
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(run, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("dart", StringComparison.Ordinal));
    }

    /// <summary>
    /// A marker names a kind of entry as well as a name. A file called like a tool's folder is
    /// something else that happens to share the name, and is left alone.
    /// </summary>
    [Fact]
    public async Task LeavesAFileNamedLikeAToolsFolderAlone()
    {
        var file = Entry(4096, "flutter_tools.ab");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(file), "a file named like a tool's folder was removed");
    }

    /// <summary>A program working inside a recognised folder holds it back whatever its tool.</summary>
    [Fact]
    public async Task HoldsBackAFolderARunningProgramIsWorkingIn()
    {
        var cache = Path.GetDirectoryName(Path.GetDirectoryName(Entry(4096, "node-compile-cache", "v1", "x")))!;

        var plan = await CreateProvider(liveTrees: new FakeLiveTreeInspector(cache)).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A link named like a marker is declined: what is on the far side was never classified.</summary>
    [Fact]
    public async Task NeverFollowsOrRemovesALinkNamedLikeAMarker()
    {
        var outside = _temp.CreateDirectory("outside");
        var kept = _temp.CreateFile(4096, "outside", "precious.bin");
        var link = Path.Combine(UserTemp, "node-compile-cache");
        SymbolicLink.ToDirectory(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(kept));
    }

    /// <summary>
    /// A configured cache is examined as Node's folder, not taken whole: the variable may point
    /// anywhere, so only Node's per-version directories are recognised in it (§5.2).
    /// </summary>
    [Fact]
    public async Task TakesOnlyNodesVersionDirectoriesFromAConfiguredCache()
    {
        var configured = _temp.CreateDirectory("profile", "node-cache");
        _temp.CreateFile(4096, "profile", "node-cache", "v24.15.0-x64-1a2b3c4d", "0a1b2c3d");
        var mine = _temp.CreateFile(2048, "profile", "node-cache", "my-notes", "todo.txt");
        var loose = _temp.CreateFile(1024, "profile", "node-cache", "keep.txt");
        _environment.WithEnvironmentVariable(TempToolCacheProvider.NodeCompileCacheVariable, configured);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>());
        Assert.Equal(Path.Combine(configured, "v24.15.0-x64-1a2b3c4d"), step.Path);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(configured, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine), "a folder in a configured cache Node did not make was removed");
        Assert.True(File.Exists(loose), "a file in a configured cache was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        Entry(2048, "flutter_tools.1a2b3c", "app.dill");
        Entry(1024, "mozilla-temp-files", "mozilla-temp-41");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// A cache <c>NODE_COMPILE_CACHE</c> moves outside every temporary folder is a place the clean
    /// reaches, and only the setting names it.
    /// </summary>
    [Fact]
    public async Task CleanedPlacesCoverAConfiguredNodeCache()
    {
        var configured = _temp.CreateDirectory("profile", "node-cache");
        _temp.CreateFile(4096, "profile", "node-cache", "v24.15.0-x64-1a2b3c4d", "0a1b2c3d");
        _environment.WithEnvironmentVariable(TempToolCacheProvider.NodeCompileCacheVariable, configured);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// A setting naming the temporary folder's own <c>node-compile-cache</c> through another mount of
    /// its volume names the folder the temporary-folder marker already takes whole. Examined again as
    /// Node's, its version folders were offered beside it and it was asserted to survive the run that
    /// removes it (§5.6). A setting naming an ordinary folder reached the same way is still Node's.
    /// </summary>
    [Theory]
    [InlineData("node-compile-cache", false)]
    [InlineData("node-cache", true)]
    public async Task ANodeCacheSettingNamingTheTemporaryFoldersCacheThroughAnotherMountIsNotExaminedTwice(
        string name, bool examined)
    {
        using var mirror = new TempDirectory();
        var volumes = new FakeVolumeInventory().With(_temp.Path + @"\", alsoMountedAt: [mirror.Path + @"\"]);
        var cache = Path.GetDirectoryName(Path.GetDirectoryName(Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d")))!;
        mirror.CreateFile(2048, "temp", name, "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        var configured = Path.Combine(mirror.Path, "temp", name);
        _environment.WithEnvironmentVariable(TempToolCacheProvider.NodeCompileCacheVariable, configured);

        var plan = await CreateProvider(volumes: volumes).PlanAsync();

        Assert.Contains(cache, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(examined, plan.ProtectedPaths.Any(p => p.Path.Equals(configured, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            examined,
            plan.TargetedPaths.Contains(Path.Combine(configured, "v26.7.0-x64-8d7ad2ee"), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A configured cache inside the temporary folder, named through another mount of its volume, is
    /// claimed from the temporary folder as it names it, so the temporary-folder row leaves Node's
    /// folder to this row rather than taking it on its age. Nothing beside it is claimed.
    /// </summary>
    [Fact]
    public async Task ClaimsAConfiguredCacheInATemporaryFolderNamedThroughAnotherMount()
    {
        using var mirror = new TempDirectory();
        var volumes = new FakeVolumeInventory().With(_temp.Path + @"\", alsoMountedAt: [mirror.Path + @"\"]);
        mirror.CreateFile(4096, "temp", "node-cache", "v24.15.0-x64-1a2b3c4d", "0a1b2c3d");
        mirror.CreateFile(1024, "temp", "unrelated", "x");
        _environment.WithEnvironmentVariable(
            TempToolCacheProvider.NodeCompileCacheVariable, Path.Combine(mirror.Path, "temp", "node-cache"));

        var claimed = await CreateProvider(volumes: volumes).ClaimedEntriesAsync([UserTemp]);

        Assert.Equal([Path.Combine(UserTemp, "node-cache")], claimed);
    }

    /// <summary>
    /// A setting that names a temporary folder or a drive root is declined, with the reason on the
    /// row. Examined as Node's, every entry beside the version folders would be a survivor, and the
    /// entries this row and the temporary-folder row take there would read as failures (§5.6).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclinesANodeCacheSettingThatNamesATemporaryFolderOrADriveRoot(bool driveRoot)
    {
        var flutter = Path.GetDirectoryName(Entry(2048, "flutter_tools.1a2b3c", "app.dill"))!;
        var setting = driveRoot ? Path.GetPathRoot(UserTemp)! : UserTemp;
        _environment.WithEnvironmentVariable(TempToolCacheProvider.NodeCompileCacheVariable, setting);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([flutter], plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(flutter, StringComparison.OrdinalIgnoreCase));
        // The drive root is named as one. The containment rule would decline this one as well, but
        // not a root that holds none of the folders it lists, such as a second drive's.
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains(TempToolCacheProvider.NodeCompileCacheVariable, StringComparison.Ordinal)
            && (!driveRoot || n.Message.Contains("root of a drive", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            await provider.DiscoverToolRootsAsync(),
            r => r.Path.Equals(Path.TrimEndingDirectorySeparator(setting), StringComparison.OrdinalIgnoreCase)
                || r.Path.Equals(setting, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A link on the way down to a tool's folder is declined like one at the top: the sessions on
    /// its far side were never classified, and the far side may be anywhere. Said once, however many
    /// of the tool's places lie behind it.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task NeverFollowsALinkOnTheWayDownToAToolsFolder(DirectoryLinkKind kind)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var kept = _temp.CreateFile(4096, "elsewhere", "AnalyzerAssemblyLoader", Session, "Analyzer.dll");
        var link = Path.Combine(UserTemp, "Roslyn");
        DirectoryLink.Create(kind, link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.Single(plan.Notes, n => n.Message.Contains(link, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(kept), "a session behind a link was removed");
        Assert.Equal([link], await provider.ClaimedEntriesAsync([UserTemp]));
    }

    /// <summary>
    /// Where the process table could not be read, nothing says a recognised folder is unused, so none
    /// is offered — "could not tell" is not "nothing is using it".
    /// </summary>
    [Fact]
    public async Task OffersNoFolderWhenItCannotTellWhatIsRunning()
    {
        var cache = Path.GetDirectoryName(Path.GetDirectoryName(Entry(4096, "node-compile-cache", "v1", "x")))!;

        var plan = await CreateProvider(liveTrees: FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined, "a row holding everything back read as already clear");
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// What this row speaks for in a temporary folder: each recognised entry, and a tool's own folder
    /// whole — so the temporary-folder row never takes a live Roslyn session on its age.
    /// </summary>
    [Fact]
    public async Task ClaimsEachRecognisedEntryAndEachToolFolderWhole()
    {
        Entry(1, "node-compile-cache", "v1", "x");
        Entry(1, "Roslyn", "AnalyzerAssemblyLoader", Session, "a.dll");
        Entry(1, "unrelated", "x");

        var claimed = await CreateProvider(mutexes: new FakeNamedMutexes(Session)).ClaimedEntriesAsync([UserTemp]);

        Assert.Equal(
            new[] { Path.Combine(UserTemp, "node-compile-cache"), Path.Combine(UserTemp, "Roslyn") }.Order(StringComparer.Ordinal),
            claimed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task IsAbsentWhereNoToolHasLeftAnything()
    {
        Entry(1, "unrelated", "x");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(await provider.ClaimedEntriesAsync([UserTemp]));
    }
}
