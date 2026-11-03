using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rows that recognise what named tools left in this account's temporary folders share one
/// listing of each folder for a planning pass, and still decide, say and protect exactly what each
/// decided on a listing of its own.
/// </summary>
public sealed class TempFolderListingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    /// <summary>How many times each folder was listed, by the folder.</summary>
    private readonly ConcurrentDictionary<string, int> _listed = new(StringComparer.OrdinalIgnoreCase);

    private readonly TempFolderListings _listings;

    public TempFolderListingsTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
        _listings = new TempFolderListings(folder =>
        {
            _listed.AddOrUpdate(Path.TrimEndingDirectorySeparator(folder), 1, (_, count) => count + 1);
            return TempFolderListings.Read(folder);
        });
    }

    public void Dispose() => _temp.Dispose();

    private string UserTemp => _environment.TempPath;

    private string Entry(int bytes, params string[] segments) => _temp.CreateFile(bytes, ["temp", .. segments]);

    /// <summary>The three rows that examine the temporary folders themselves, sharing one listing.</summary>
    private IReadOnlyList<TempMarkerProviderBase> Rows(IProcessInspector? inspector = null)
    {
        inspector ??= FakeProcessInspector.NothingRunning;

        return
        [
            new TempToolCacheProvider(
                _environment, new FakeProcessRunner(), inspector, system: _system,
                liveTrees: FakeLiveTreeInspector.NothingLive, mutexes: FakeNamedMutexes.None, volumes: _volumes,
                listings: _listings),
            new TempInstallerDownloadProvider(
                _environment, new FakeProcessRunner(), inspector, system: _system,
                liveTrees: FakeLiveTreeInspector.NothingLive, volumes: _volumes, listings: _listings),
            new TempToolLogProvider(
                _environment, new FakeProcessRunner(), inspector, system: _system,
                liveTrees: FakeLiveTreeInspector.NothingLive, volumes: _volumes, listings: _listings),
        ];
    }

    private CleanupPlanner Planner(IReadOnlyList<TempMarkerProviderBase> rows) => new(rows, volumes: _volumes);

    /// <summary>
    /// Every row starts from the temporary folder itself, and a developer's holds tens of thousands of
    /// entries. One pass lists it once, however many rows read it.
    /// </summary>
    [Fact]
    public async Task OnePassListsEachFolderOnceForEveryRow()
    {
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        Entry(2048, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe");
        Entry(512, "vscode-inno-updater-1756000000.log");

        var rows = Rows();
        var findings = await Planner(rows).PlanAllAsync();

        Assert.All(findings, finding => Assert.NotEmpty(finding.Plan!.Steps));
        Assert.Equal(1, _listed.GetValueOrDefault(UserTemp));
        Assert.All(_listed, listed => Assert.True(listed.Value == 1, $"'{listed.Key}' was listed {listed.Value} times in one pass"));
    }

    /// <summary>
    /// A folder that refused to be listed is asked once, and the refusal reaches every row: each says
    /// it could not read the folder, and none reads as having found it empty.
    /// </summary>
    [Fact]
    public async Task ARefusalIsListedOnceAndEveryRowSaysItCouldNotRead()
    {
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");

        using var denied = new DeniedDirectory(UserTemp);

        var findings = await Planner(Rows()).PlanAllAsync();

        Assert.Equal(1, _listed.GetValueOrDefault(UserTemp));
        Assert.Equal(3, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.True(finding.IsPresent, $"{finding.Provider.Id} hid the refusal by reading as absent");
            Assert.True(finding.Plan!.HasUnreadableRoot, $"{finding.Provider.Id} did not say a folder was unread");
            Assert.Contains(finding.Plan.Notes, note => note == UnreadableRoot.Note(UserTemp));
        });
    }

    /// <summary>
    /// §5.6 and §5.2 for rows reading one listing: what each recognises goes, and the temporary folder,
    /// what nobody recognises in it, what a tool's own folder holds unrecognised and what a running
    /// tool may be using all survive and are asserted to.
    /// </summary>
    [Fact]
    public async Task RowsReadingOneListingLeaveEverySurvivorStanding()
    {
        var nodeCache = Path.Combine(UserTemp, "node-compile-cache");
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        var updaterLog = Entry(512, "vscode-inno-updater-1756000000.log");
        var hubLog = Entry(1024, "servicehub", "logs", "0a1b2c3d-VsHubClient-1234-abcdefgh-1.log");
        var hubOther = Entry(128, "servicehub", "settings.json");
        var download = Path.GetDirectoryName(Entry(2048, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe"))!;
        var stranger = Entry(8192, "q4mzt0xk", "payload.cab");

        var planner = Planner(Rows(new FakeProcessInspector("Code")));
        var findings = await planner.PlanAllAsync();

        var installers = findings.Single(f => f.Provider is TempInstallerDownloadProvider).Plan!;
        Assert.Contains(installers.ProtectedPaths, p => p.Path.Equals(download, StringComparison.OrdinalIgnoreCase));
        var logs = findings.Single(f => f.Provider is TempToolLogProvider).Plan!;
        Assert.Contains(logs.ProtectedPaths, p => p.Path.Equals(hubOther, StringComparison.OrdinalIgnoreCase));

        var results = new List<CleanupResult>();

        foreach (var finding in findings.Where(f => f.HasSomethingToRemove))
        {
            results.Add(await finding.Provider.ExecuteAsync(finding.Plan!));
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Verification!.Passed, result.Verification.Summary));
        Assert.False(Directory.Exists(nodeCache));
        Assert.False(File.Exists(updaterLog));
        Assert.False(File.Exists(hubLog));
        Assert.True(Directory.Exists(UserTemp), "the temporary folder itself was removed");
        Assert.True(File.Exists(stranger), "an entry no row recognises was removed");
        Assert.True(File.Exists(hubOther), "an unrecognised entry in a tool's own folder was removed");
        Assert.True(Directory.Exists(download), "a download a running tool may be using was removed");
    }

    /// <summary>
    /// The listing lasts the pass and no longer: the next pass lists the folder again and sees what was
    /// added between, and nothing holds tens of thousands of entries while the app sits idle.
    /// </summary>
    [Fact]
    public async Task TheNextPassListsTheFolderAgain()
    {
        var rows = Rows();
        var planner = Planner(rows);

        var first = await planner.PlanAllAsync();
        Assert.All(first, finding => Assert.False(finding.IsPresent));

        var nodeCache = Path.Combine(UserTemp, "node-compile-cache");
        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");

        var second = await planner.PlanAllAsync();

        Assert.Equal(2, _listed.GetValueOrDefault(UserTemp));
        Assert.Contains(
            second.Single(f => f.Provider is TempToolCacheProvider).Plan!.Steps.OfType<DeleteStep>(),
            step => step.Path.Equals(nodeCache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Each row's hold on the pass is a hold on the shared listing, so the listing goes when the pass
    /// ends rather than staying in memory until the next one begins.
    /// </summary>
    [Fact]
    public async Task ThePassDropsTheListingWhenItEnds()
    {
        await Planner(Rows()).PlanAllAsync();

        _listings.Of(UserTemp);

        Assert.Equal(2, _listed.GetValueOrDefault(UserTemp));
    }

    /// <summary>
    /// A row asked outside a planning pass keeps the listing until it is invalidated, as it keeps its
    /// own findings, and invalidating it drops the shared listing too, so it sees what was added since.
    /// </summary>
    [Fact]
    public async Task InvalidatingARowDropsTheSharedListing()
    {
        var caches = Rows()[0];
        Assert.False(await caches.IsPresentAsync());

        Entry(4096, "node-compile-cache", "v26.7.0-x64-8d7ad2ee", "0a1b2c3d");
        caches.InvalidateCaches();

        Assert.True(await caches.IsPresentAsync());
        Assert.Equal(2, _listed.GetValueOrDefault(UserTemp));
    }

    /// <summary>
    /// The hold is counted, because every row of a pass takes one: the first row to finish must not
    /// take the listing from the rest, and the last to finish drops it.
    /// </summary>
    [Fact]
    public void AListingIsKeptUntilTheLastHoldEnds()
    {
        var first = _listings.Hold();
        var second = _listings.Hold();

        _listings.Of(UserTemp);
        first.Dispose();
        _listings.Of(UserTemp);

        Assert.Equal(1, _listed.GetValueOrDefault(UserTemp));

        second.Dispose();
        _listings.Of(UserTemp);

        Assert.Equal(2, _listed.GetValueOrDefault(UserTemp));
    }

    /// <summary>
    /// Explore reads the folders as they are whenever it rebuilds its policy, so it lists them afresh
    /// rather than reading a pass's listing: a download that appeared while the pass's listing was still
    /// held is refused there all the same, because the tool that downloaded it is running.
    /// </summary>
    [Fact]
    public async Task ExploreListsTheFoldersAfreshRatherThanThroughThePass()
    {
        var installers = Rows(new FakeProcessInspector("Code")).Single(r => r is TempInstallerDownloadProvider);
        using var pass = _listings.Hold();

        await Planner([installers]).PlanAllAsync();

        var download = Path.GetDirectoryName(Entry(2048, "vscode-stable-user-x64", "CodeSetup-stable-1.105.0.exe"))!;
        var roots = await installers.DiscoverToolRootsAsync();

        Assert.Equal(1, _listed.GetValueOrDefault(UserTemp));
        Assert.Contains(roots, root => root.Path.Equals(download, StringComparison.OrdinalIgnoreCase));
    }
}
