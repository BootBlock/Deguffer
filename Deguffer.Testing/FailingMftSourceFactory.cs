using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// One volume whose open fails once the other's table read has begun, and the other's read held until
/// its token is cancelled, so work on the two volumes at once ends only if the failure stops the read.
/// </summary>
/// <param name="failing">The volume whose open throws <see cref="Failure"/>.</param>
/// <param name="held">The volume whose table read waits on nothing but its token.</param>
public sealed class FailingMftSourceFactory(MftFixture fixture, char failing, char held) : IMftSourceFactory
{
    public const string Failure = "The volume driver failed.";

    /// <summary>How long the failing open waits for the held read to begin before it fails anyway.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IMftVolume? TryOpen(char driveLetter, out FallbackReason reason, MftOpenTimings? timings = null)
    {
        reason = FallbackReason.None;

        if (char.ToUpperInvariant(driveLetter) == failing)
        {
            _reading.Task.Wait(Patience);
            throw new InvalidOperationException(Failure);
        }

        return char.ToUpperInvariant(driveLetter) == held ? new ServedVolume(new Held(fixture.Build(), _reading)) : null;
    }

    private sealed class Held(IMftSource inner, TaskCompletionSource reading) : IMftSource
    {
        public int BytesPerRecord => inner.BytesPerRecord;

        public long RecordCount => inner.RecordCount;

        public MftBitmapPlacement? Bitmap => inner.Bitmap;

        public int BytesPerCluster => inner.BytesPerCluster;

        public int BatchLength(long firstRecord, int capacity) => inner.BatchLength(firstRecord, capacity);

        public int ReadBatch(long firstRecord, Span<byte> destination) => inner.ReadBatch(firstRecord, destination);

        public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        public bool TryReadClusters(long firstCluster, Span<byte> destination) => inner.TryReadClusters(firstCluster, destination);

        public ValueTask<bool> TryReadClustersAsync(long firstCluster, Memory<byte> destination, CancellationToken ct) =>
            inner.TryReadClustersAsync(firstCluster, destination, ct);

        public void Dispose() => inner.Dispose();
    }
}
