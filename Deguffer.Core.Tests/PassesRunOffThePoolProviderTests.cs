using Deguffer.Core.Cloud;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walks a provider starts from async code, each handed to a thread of the walk's own rather than
/// run on the thread pool: a walk holds the thread that drives it for its whole length, and a preview
/// plans up to eight providers at once. <see cref="PassesRunOffThePoolTests"/> pins the scanners and the
/// removals; these pin the providers' own walks, each at the place it is started.
///
/// <para>Each asserts that a crew was made at all, so a fixture that never reached the walk fails
/// rather than passing on an empty list.</para>
/// </summary>
public sealed class PassesRunOffThePoolProviderTests : IDisposable
{
    private const string OneDriveId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Personal";

    private const VolumeFeatures LocalDisk = (VolumeFeatures)0x03E7_2EFF;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PassesRunOffThePoolProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// <see cref="ChromiumUserDataProvider.IsPresentAsync"/>: the first ask of a fresh provider walks
    /// both application-data roots for the folders it looks inside.
    /// </summary>
    [Fact]
    public async Task AChromiumRowsPresenceIsWalkedOffThePool()
    {
        ChromiumApplication("Chatter");
        var present = false;

        var made = await CrewsMadeBy(async () => present = await ChromiumProvider().IsPresentAsync());

        Assert.True(present);
        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="ChromiumUserDataProvider"/>'s plan, reached through
    /// <see cref="CleanupProviderBase.PlanAsync"/>: a fresh provider's plan is where the walk is made.
    /// No recorded refusal, so the plan's own walk is the only one.
    /// </summary>
    [Fact]
    public async Task AChromiumRowsPlanIsWalkedOffThePool()
    {
        var made = await CrewsMadeBy(async () =>
        {
            var plan = await ChromiumProvider().PlanAsync();

            Assert.Empty(plan.Steps);
        });

        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="ChromiumUserDataProvider.CleanedPlacesAsync"/>, which walks for every folder the
    /// discovery finds rather than asking the memoised applications.
    /// </summary>
    [Fact]
    public async Task AChromiumRowsCleanedPlacesAreWalkedOffThePool()
    {
        ChromiumApplication("Chatter");
        IReadOnlyList<CleanedPlace> places = [];

        var made = await CrewsMadeBy(async () => places = await ChromiumProvider().CleanedPlacesAsync());

        Assert.NotEmpty(places);
        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="CleanupProviderBase.PlanAsync"/>'s last hop, <see cref="RecordedRefusals"/>: a
    /// place a previous clean was refused in is walked again. The provider's own plan walks nothing, so
    /// the only crew is the one asking again.
    /// </summary>
    [Fact]
    public async Task ARecordedRefusalIsAskedAgainOffThePool()
    {
        var step = _temp.CreateDirectory("scratch");
        var place = _temp.CreateDirectory("scratch", "held");
        _temp.CreateFile(64, "scratch", "held", "a.bin");
        RefusalRecord.For(_environment).Replace(step, [place]);

        var provider = new FixedPlanProvider(_environment, new DeleteDirectoryStep(step, "Scratch files")
        {
            Estimated = ScanSize.FromLengths(64),
        });

        var made = await CrewsMadeBy(async () => Assert.Single((await provider.PlanAsync()).Steps));

        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="SourceDirectoryDiscovery"/>'s walk after the index declines, for a scanner that
    /// waits for the index rather than racing it.
    /// </summary>
    [Fact]
    public async Task ASourceFolderIsWalkedOffThePoolWhenTheIndexDeclines()
    {
        var root = SourceProject();
        var scanner = new FakeDirectoryScanner();
        var discovery = SourceDiscovery(scanner);
        SourceDiscovery? found = null;

        var made = await CrewsMadeBy(async () => found = await discovery.FindAsync([new SourceRoot(root)]));

        Assert.Equal(1, scanner.FindCalls);
        Assert.True(found!.FellBack);
        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="SourceDirectoryDiscovery"/>'s walk raced against the index. The index declines, so
    /// the walk runs to its end whatever the order the two start in.
    /// </summary>
    [Fact]
    public async Task ASourceFolderIsWalkedOffThePoolWhileRacingTheIndex()
    {
        var root = SourceProject();
        var discovery = SourceDiscovery(new RacingScanner(new FakeDirectoryScanner()));
        SourceDiscovery? found = null;

        var made = await CrewsMadeBy(async () => found = await discovery.FindAsync([new SourceRoot(root)]));

        Assert.True(found!.FellBack);
        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>
    /// <see cref="CloudLocalCopiesProvider"/>'s walk of a running sync app's folder for the local
    /// copies it can release.
    /// </summary>
    [Fact]
    public async Task ASyncFolderIsWalkedOffThePool()
    {
        var root = _temp.CreateDirectory("OneDrive");
        var cloud = new FakeCloudFiles();
        cloud.Root(OneDriveId, root, "OneDrive - Personal")
            .Folder(Path.Combine(root, "Photos"))
            .File(Path.Combine(root, "Photos", "beach.jpg"), onDisk: 6_000);

        var provider = new CloudLocalCopiesProvider(
            cloud, _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner());

        var made = await CrewsMadeBy(async () =>
            Assert.IsType<ReleaseLocalCopiesStep>(Assert.Single((await provider.PlanAsync()).Steps)));

        Assert.NotEmpty(made);
        Assert.DoesNotContain(true, made);
    }

    /// <summary>Whether each crew <paramref name="act"/> made was made on a pool thread.</summary>
    private static async Task<IReadOnlyList<bool>> CrewsMadeBy(Func<Task> act)
    {
        var made = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        WorkerCrew.Made.Value = made.Enqueue;
        try { await act(); }
        finally { WorkerCrew.Made.Value = null; }
        return [.. made];
    }

    /// <summary>A fresh row, so the memoised discovery walks on the first ask.</summary>
    private ChromiumCacheProvider ChromiumProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, liveTrees: FakeLiveTreeInspector.NothingLive);

    /// <summary>A folder holding Chromium's <c>Local State</c> marker, with a web cache beside it.</summary>
    private void ChromiumApplication(string name)
    {
        var path = Path.Combine(_environment.RoamingAppData, name);
        Directory.CreateDirectory(Path.Combine(path, "GPUCache"));
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
    }

    /// <summary>A folder holding one project with an <c>obj</c> in it, which is what the search looks for.</summary>
    private string SourceProject()
    {
        var root = _temp.CreateDirectory("src");
        _temp.CreateDirectory("src", "Example", "obj");

        return root;
    }

    /// <summary>A search for <c>obj</c> over a machine whose only volume is the scratch tree's, a local disk.</summary>
    private SourceDirectoryDiscovery SourceDiscovery(IDirectoryScanner scanner)
    {
        var discovery = new SourceDirectoryDiscovery(
            scanner, new FakeVolumeInventory().With(Path.GetPathRoot(_temp.Path)!, features: LocalDisk));

        discovery.Include(["obj"]);

        return discovery;
    }

    /// <summary>A provider whose plan is the step it is handed, made without walking anything.</summary>
    private sealed class FixedPlanProvider(IUserEnvironment environment, CleanupStep step)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner())
    {
        public override string Id => "fixed-plan";

        public override string Name => "Fixed plan";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderDescription Description { get; } = new()
        {
            Category = ProviderCategory.TemporaryFiles,
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Plans the step it is handed.",
            Recommendation = "None.",
        };

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        protected override Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct) =>
            Task.FromResult(new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = [step],
            });
    }

    /// <summary>
    /// A scanner that races the walk against an index which always declines, as the shipped scanner
    /// does on <see cref="ScanRoute.Auto"/> while a volume's table is unreadable.
    /// </summary>
    private sealed class RacingScanner(FakeDirectoryScanner inner) : IDirectoryScanner
    {
        public bool RacesTheWalk => true;

        public ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            ScanBounds bounds = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            inner.MeasureAsync(path, keep, bounds, progress, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked,
            MinimumAge keep = default,
            CancellationToken ct = default) =>
            inner.MeasureManyAsync(asked, keep, ct);

        public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
            inner.MeasureAgedAsync(path, age, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            inner.MeasureFromDiskAsync(asked, ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
            DirectorySearch search,
            string root,
            CancellationToken ct = default) =>
            inner.TryFindDirectoriesAsync(search, root, ct);

        public void Invalidate() => inner.Invalidate();
    }
}
