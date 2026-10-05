using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// One volume whose table is not read until the test lets it be, so a test can ask a question while
/// the volume's index is still being built and know which route answers it.
///
/// <para>The open succeeds at once, as an elevated one does, and every read of the table waits for
/// <see cref="Release"/>.</para>
/// </summary>
public sealed class HeldMftSourceFactory(char driveLetter, MftFixture fixture) : IMftSourceFactory
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _opened;
    private int _closed;

    /// <summary>How many times the volume was opened.</summary>
    public int OpenCount => Volatile.Read(ref _opened);

    /// <summary>How many of those opens have been closed again.</summary>
    public int CloseCount => Volatile.Read(ref _closed);

    /// <summary>Let every read, waiting or still to come, go ahead.</summary>
    public void Release() => _released.TrySetResult();

    public IMftSource? TryOpen(char letter, out FallbackReason reason)
    {
        if (char.ToUpperInvariant(letter) != char.ToUpperInvariant(driveLetter))
        {
            reason = FallbackReason.NotNtfsVolume;
            return null;
        }

        Interlocked.Increment(ref _opened);
        reason = FallbackReason.None;

        return new Held(fixture.Build(), this);
    }

    private sealed class Held(IMftSource inner, HeldMftSourceFactory owner) : IMftSource
    {
        public int BytesPerRecord => inner.BytesPerRecord;

        public long RecordCount => inner.RecordCount;

        public MftBitmapPlacement? Bitmap => inner.Bitmap;

        public int BytesPerCluster => inner.BytesPerCluster;

        public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination)
        {
            owner._released.Task.Wait();
            return inner.ReadBatch(firstRecord, destination);
        }

        public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
        {
            await owner._released.Task.WaitAsync(ct).ConfigureAwait(false);
            return await inner.ReadBatchAsync(firstRecord, destination, ct).ConfigureAwait(false);
        }

        public bool TryReadClusters(long firstCluster, Span<byte> destination)
        {
            owner._released.Task.Wait();
            return inner.TryReadClusters(firstCluster, destination);
        }

        public void Dispose()
        {
            inner.Dispose();
            Interlocked.Increment(ref owner._closed);
        }
    }
}
