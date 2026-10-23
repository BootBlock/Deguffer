using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A volume index is kept for the planning pass that built it and no longer. It costs about 49 bytes
/// a slot of the volume's table, over a hundred megabytes on a system volume, and nothing reads it
/// once the pass has ended, because the next pass drops it before it plans.
///
/// <para>Each test sees an index dropped by the volume being opened again for the next question,
/// which is the only way a scanner has of rebuilding one.</para>
/// </summary>
public sealed class VolumeIndexLifetimeTests
{
    private const string Cache = @"\Users\testuser\.npm-cache";

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(7, 6, "testuser")
        .AddDirectory(8, 7, ".npm-cache")
        .AddFile(20, 8, "a.tgz", allocated: 8192, logical: 8000);

    [Fact]
    public async Task AHeldIndexAnswersEveryQuestionOfThePassFromOneBuild()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(letter + @":" + Cache);
            await scanner.MeasureAsync(letter + @":\Users");
        }

        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task TheIndexIsDroppedWhenTheLastHoldEnds()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(letter + @":" + Cache);
        }

        await scanner.MeasureAsync(letter + @":" + Cache);

        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>
    /// Every provider of a pass holds the scanner they share. The first to give its hold back must not
    /// take the index from the rest, and ending the same hold twice must not count as a second hold
    /// ending.
    /// </summary>
    [Fact]
    public async Task TheIndexIsKeptWhileAnotherHoldStands()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        var first = scanner.Hold();
        var second = scanner.Hold();
        await scanner.MeasureAsync(letter + @":" + Cache);

        first.Dispose();
        first.Dispose();
        await scanner.MeasureAsync(letter + @":" + Cache);
        Assert.Equal(1, factory.OpenCount);

        second.Dispose();
        await scanner.MeasureAsync(letter + @":" + Cache);
        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>
    /// A build still reading the table when the last hold ends is stopped, and ends cancelled rather
    /// than faulted. Nothing awaits it once its index is dropped, and a faulted task nobody awaits is
    /// recorded as a crash when the collector finds it. It still closes the volume.
    /// </summary>
    [Fact]
    public async Task ABuildStoppedByTheLastHoldEndsCancelledNotFaulted()
    {
        var letter = DriveLetters.Unused();
        var sources = new HeldMftSourceFactory(letter, Volume());
        var cache = new MftVolumeIndexCache(sources, ScanTuner.Shipped);

        var hold = cache.Hold();
        var building = cache.Get(letter);
        hold.Dispose();
        sources.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(building.IsCanceled, $"The stopped build ended {building.Status}.");
        Assert.Equal(1, sources.CloseCount);
    }

    /// <summary>
    /// The same for a build stopped while it reads a file's attribute list kept outside the table. That
    /// read stops on a token of its own, linked to the scan's, so what it throws names the wrong token
    /// for the build's task to end cancelled on.
    /// </summary>
    [Fact]
    public async Task ABuildStoppedReadingAnAttributeListEndsCancelledNotFaulted()
    {
        const long ListCluster = 900;
        var letter = DriveLetters.Unused();
        var sources = new ListHeldOpen(
            new MftFixture().AddFileWhoseListOutsideTheTableNamesRecords(16, ListCluster, firstNamed: 1_000, count: 4),
            ListCluster);
        var cache = new MftVolumeIndexCache(sources, ScanTuner.Shipped);

        var hold = cache.Hold();
        var building = cache.Get(letter);
        await sources.ListReadStarted.WaitAsync(TimeSpan.FromSeconds(30));
        hold.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(building.IsCanceled, $"The stopped build ended {building.Status}.");
        Assert.Equal(1, sources.CloseCount);
    }

    /// <summary>
    /// The whole route: a provider measures through the scanner during a pass, and once the pass has
    /// ended the scanner keeps no index, though nothing invalidated it.
    /// </summary>
    [Fact]
    public async Task APlanningPassLeavesNoIndexBehind()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);
        using var temp = new TempDirectory();
        var provider = new MeasuringProvider(new FakeUserEnvironment(temp.Path), scanner, letter + @":" + Cache);

        await new CleanupPlanner([provider]).PlanAllAsync();

        Assert.Equal(ScanStrategy.MasterFileTable, provider.Measured?.Strategy);
        Assert.Equal(1, factory.OpenCount);

        await scanner.MeasureAsync(letter + @":" + Cache);

        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>A provider holds the scanner it measures with, and the pass gives the hold back.</summary>
    [Fact]
    public async Task AProviderHoldsItsScannerForThePass()
    {
        using var temp = new TempDirectory();
        var scanner = new FakeDirectoryScanner();
        var provider = new MeasuringProvider(new FakeUserEnvironment(temp.Path), scanner, temp.CreateDirectory("cache"));

        await new CleanupPlanner([provider]).PlanAllAsync();

        Assert.Equal(1, scanner.HoldsTaken);
        Assert.Equal(0, scanner.HoldsStanding);
    }

    /// <summary>
    /// A volume whose table reads at once, but whose read of the cluster holding a file's attribute list
    /// waits until it is cancelled.
    /// </summary>
    private sealed class ListHeldOpen(MftFixture fixture, long listCluster) : IMftSourceFactory
    {
        private readonly TaskCompletionSource _listReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _listCluster = listCluster;
        private int _closed;

        public Task ListReadStarted => _listReadStarted.Task;

        public int CloseCount => Volatile.Read(ref _closed);

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            reason = FallbackReason.None;

            return new Held(fixture.Build(), this);
        }

        private sealed class Held(IMftSource table, ListHeldOpen owner) : IMftSource
        {
            public int BytesPerRecord => table.BytesPerRecord;

            public long RecordCount => table.RecordCount;

            public MftBitmapPlacement? Bitmap => table.Bitmap;

            public int BytesPerCluster => table.BytesPerCluster;

            public int BatchLength(long firstRecord, int capacity) => table.BatchLength(firstRecord, capacity);

            public int ReadBatch(long firstRecord, Span<byte> destination) => table.ReadBatch(firstRecord, destination);

            public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
                table.ReadBatchAsync(firstRecord, destination, ct);

            public bool TryReadClusters(long firstCluster, Span<byte> destination) => table.TryReadClusters(firstCluster, destination);

            public async ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct)
            {
                if (firstCluster == owner._listCluster)
                {
                    owner._listReadStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }

                return await table.TryReadClustersAsync(firstCluster, destination, ct).ConfigureAwait(false);
            }

            public void Dispose()
            {
                table.Dispose();
                Interlocked.Increment(ref owner._closed);
            }
        }

    }

    /// <summary>Measures one path twice through its scanner, as several rows of one pass would, and plans nothing.</summary>
    private sealed class MeasuringProvider(IUserEnvironment environment, IDirectoryScanner scanner, string path)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner)
    {
        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        public override string Id => "measuring";

        public override string Name => "Measuring";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderDescription Description { get; } = new()
        {
            Category = ProviderCategory.TemporaryFiles,
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Measures one path.",
            Recommendation = "None.",
        };

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        /// <summary>The second reading of the pass.</summary>
        public ScanResult? Measured { get; private set; }

        protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
        {
            await Scanner.MeasureAsync(path, keep, ct: ct);
            Measured = await Scanner.MeasureAsync(path, keep, ct: ct);

            return new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = [],
            };
        }
    }
}
