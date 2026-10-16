using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A pass over a file table, or a walk, holds the thread that drives it for its whole length, so none
/// is driven from the thread pool: a preview building the index of more volumes than the machine has
/// processors would hold the pool's every thread, and each pass past its minimum would wait for it to
/// grow.
///
/// <para>A table's record count is read once, by the thread that drives the pass, as it starts, and a
/// walk's first directory is listed by the thread that drives the walk. <see cref="WorkerThreadsTests"/>
/// proves that the threads these run on start at once however many run together. These pin that each
/// is handed to them.</para>
/// </summary>
public sealed class PassesRunOffThePoolTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddFile(20, 6, "a.bin", allocated: 4096, logical: 4000);

    [Fact]
    public async Task AVolumesIndexIsBuiltOffThePool()
    {
        var letter = UnusedDriveLetter();
        var sources = new DrivingThreads(letter);

        var built = await new MftVolumeIndexCache(sources, ScanTuner.Shipped).Get(letter);

        Assert.NotNull(built.Index);
        Assert.False(sources.DrivenFromThePool);
    }

    [Fact]
    public async Task AnExploreScanReadsItsTableOffThePool()
    {
        var letter = UnusedDriveLetter();
        var sources = new DrivingThreads(letter);

        var scan = await new ExploreScanner(sources).ScanAsync($@"{letter}:\");

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.False(sources.DrivenFromThePool);
    }

    [Fact]
    public async Task AFolderScanReadsItsTableOffThePool()
    {
        var letter = UnusedDriveLetter();
        var sources = new DrivingThreads(letter);

        var scan = Assert.Single(await new ExploreScanner(sources).ScanFoldersAsync([$@"{letter}:\Users"]));

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.False(sources.DrivenFromThePool);
    }

    /// <summary>The walk a removal makes of a folder before it takes anything from it.</summary>
    [Fact]
    public async Task ADirectoryIsWalkedForItsRemovalOffThePool()
    {
        var folder = _temp.CreateDirectory("profile", "cache");
        _temp.CreateFile(64, "profile", "cache", "a.bin");
        var pooled = new ConcurrentBag<bool>();

        var outcome = await DirectoryRemover.RemoveAsync(
            folder,
            fileSystem: new ListingHookFileSystem(WindowsFileSystem.Default, folder, () => pooled.Add(Thread.CurrentThread.IsThreadPoolThread)));

        Assert.True(outcome.RootRemoved);
        Assert.NotEmpty(pooled);
        Assert.DoesNotContain(true, pooled);
    }

    /// <summary>The look inside a folder for an Outlook data file, before the Recycle Bin is asked to take it.</summary>
    [Fact]
    public async Task AFolderIsLookedInsideOffThePoolBeforeTheRecycleBinTakesIt()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "old");
        _temp.CreateFile(64, "profile", "Downloads", "old", "a.bin");
        var system = new FakeSystemDirectories(_temp.Path);
        var environment = new FakeUserEnvironment(_temp.Path);
        var policy = new ExploreActionPolicy(ProtectedRegions.For(system, environment), [], new FakeVolumeInventory());
        var bin = new FakeRecycleBin();
        var pooled = new ConcurrentBag<bool>();

        await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 64)],
            ExploreRemovalMode.RecycleBin,
            policy,
            bin,
            fileSystem: new ListingHookFileSystem(WindowsFileSystem.Default, folder, () => pooled.Add(Thread.CurrentThread.IsThreadPoolThread)));

        Assert.Equal(folder, Assert.Single(bin.Paths));
        Assert.NotEmpty(pooled);
        Assert.DoesNotContain(true, pooled);
    }

    /// <summary>
    /// A drive letter no volume answers to, so a scan that left the table would find nothing to walk
    /// rather than walk a real disk.
    /// </summary>
    private static char UnusedDriveLetter() =>
        "ZYXWVUTSRQPONM".First(letter => !Directory.Exists($@"{letter}:\"));

    /// <summary>One volume serving <see cref="Volume"/>, noting whether a pass over it was driven from a pool thread.</summary>
    private sealed class DrivingThreads(char volume) : IMftSourceFactory
    {
        private int _countsRead;
        private int _fromThePool;

        /// <summary>Whether a pass read the record count from a pool thread, or never read it at all.</summary>
        public bool DrivenFromThePool => Volatile.Read(ref _countsRead) == 0 || Volatile.Read(ref _fromThePool) > 0;

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            reason = driveLetter == volume ? FallbackReason.None : FallbackReason.NotNtfsVolume;

            return driveLetter == volume ? new Noting(this, Volume().Build()) : null;
        }

        private sealed class Noting(DrivingThreads owner, IMftSource inner) : IMftSource
        {
            public int BytesPerRecord => inner.BytesPerRecord;

            public long RecordCount
            {
                get
                {
                    Interlocked.Increment(ref owner._countsRead);
                    if (Thread.CurrentThread.IsThreadPoolThread)
                    {
                        Interlocked.Increment(ref owner._fromThePool);
                    }

                    return inner.RecordCount;
                }
            }

            public int BytesPerCluster => inner.BytesPerCluster;

            public MftBitmapPlacement? Bitmap => inner.Bitmap;

            public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

            public int ReadBatch(long firstRecord, Span<byte> destination) => inner.ReadBatch(firstRecord, destination);

            public ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct) =>
                inner.ReadBatchAsync(firstRecord, destination, ct);

            public bool TryReadClusters(long firstCluster, Span<byte> destination) => inner.TryReadClusters(firstCluster, destination);

            public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct) =>
                inner.TryReadClustersAsync(firstCluster, destination, ct);

            public void Dispose() => inner.Dispose();
        }
    }
}
