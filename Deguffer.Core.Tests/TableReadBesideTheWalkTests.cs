using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A volume's index cache opens the volume where it is asked, and reads where its table is on the
/// build's thread. The first read of a table through a volume handle makes NTFS flush and purge what it
/// has cached for every file on the volume, which took 0.8 to 1.6 s after a walk where it was measured (#544), so a
/// cache that read it in the open would make the walk raced against the build wait for the flush
/// before it started.
/// </summary>
public sealed class TableReadBesideTheWalkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddFile(20, 6, "a.tgz", allocated: 8192, logical: 8000);

    [Fact]
    public async Task TheBuildIsHandedBackBeforeTheTableIsRead()
    {
        const char letter = DriveLetters.Faked;
        var volume = new TableHeldShut(new ServedVolume(Volume().Build()));
        var cache = new MftVolumeIndexCache(new OneVolume(letter, volume), ScanTuner.Shipped);
        using var hold = cache.Hold();

        try
        {
            // Asked on a thread of its own, so a Get that waited for the table times out here rather
            // than holding the test.
            var asking = 0;
            var building = await Task.Factory.StartNew(
                () =>
                {
                    asking = Environment.CurrentManagedThreadId;
                    return cache.Get(letter);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).WaitAsync(Patience);

            await volume.ReadStarted.WaitAsync(Patience);
            Assert.False(building.IsCompleted, "The build ended while its table was still held shut.");

            volume.Release();
            var built = await building.WaitAsync(Patience);

            Assert.NotNull(built.Index);
            Assert.NotEqual(asking, volume.ReadThread);
        }
        finally
        {
            // A cache that read the table in the open is still waiting in it, on the asking thread.
            volume.Release();
        }
    }

    /// <summary>
    /// A table the build cannot find is a reason the build gives, as one the open gives, and leaves no
    /// index to answer from.
    /// </summary>
    [Fact]
    public async Task ATableTheBuildCannotFindSaysWhy()
    {
        const char letter = DriveLetters.Faked;
        var cache = new MftVolumeIndexCache(new OneVolume(letter, new TableNotFound()), ScanTuner.Shipped);
        using var hold = cache.Hold();

        var built = await cache.Get(letter).WaitAsync(Patience);

        Assert.Null(built.Index);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, built.Reason);
    }

    private sealed class OneVolume(char letter, IMftVolume volume) : IMftSourceFactory
    {
        public IMftVolume? TryOpen(char driveLetter, out FallbackReason reason, MftOpenTimings? timings = null)
        {
            reason = driveLetter == letter ? FallbackReason.None : FallbackReason.NotNtfsVolume;

            return driveLetter == letter ? volume : null;
        }
    }

    /// <summary>A volume whose table is not read until the test lets it be, noting the thread that read it.</summary>
    private sealed class TableHeldShut(IMftVolume inner) : IMftVolume
    {
        private readonly ManualResetEventSlim _released = new();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readThread;

        public Task ReadStarted => _readStarted.Task;

        public int ReadThread => Volatile.Read(ref _readThread);

        public void Release() => _released.Set();

        public IMftSource? TryReadTable(out FallbackReason reason)
        {
            Volatile.Write(ref _readThread, Environment.CurrentManagedThreadId);
            _readStarted.TrySetResult();
            _released.Wait(Patience);

            return inner.TryReadTable(out reason);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class TableNotFound : IMftVolume
    {
        public IMftSource? TryReadTable(out FallbackReason reason)
        {
            reason = FallbackReason.MasterFileTableIncomplete;
            return null;
        }

        public void Dispose()
        {
        }
    }
}
