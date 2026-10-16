using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Reads one lane's files on as many threads as the lane allows: the calling thread, and the rest
/// a <see cref="WorkerCrew"/>'s helpers, each taking the next file until none is left.
///
/// <para><b>Not the thread pool.</b> A read blocks its thread for as long as the disk takes. On the
/// pool, a lane's readers are as many as the pool has threads free, and the pool starts threads past
/// its minimum, one per processor, only once it finds itself starved, about one every half second.
/// Until it does, a lane bound to four readers can be read by fewer, as the content-matching tests saw
/// with the processor count held at four, and the rest of the process's pool work waits behind the
/// reads. On threads of their own
/// (<see cref="WorkerThreads"/>) the lane is read by its bound from the first file.</para>
///
/// <para><b>Stops taking files</b> once the search is stopped or a reader has failed. A read already
/// under way finishes, and nothing is thrown until every reader has.</para>
/// </summary>
internal sealed class LaneReaders<T>
{
    private readonly IReadOnlyList<T> _files;
    private readonly Action<T, CancellationToken> _read;
    private readonly CancellationToken _ct;
    private readonly WorkerCrew _crew;
    private readonly int _helpersWanted;

    private int _next = -1;

    private LaneReaders(ReadingLane<T> lane, Action<T, CancellationToken> read, CancellationToken ct)
    {
        _files = lane.Files;
        _read = read;
        _ct = ct;

        // No more helpers than files to give them, the calling thread taking one of its own.
        _helpersWanted = Math.Min(lane.Readers, lane.Files.Count) - 1;

        Action readWhileAny = ReadWhileAny;
        _crew = new WorkerCrew(lane.Readers, () => readWhileAny, () => !Stopping && Volatile.Read(ref _next) + 1 < _files.Count);
    }

    private bool Stopping => _crew.Failed || _ct.IsCancellationRequested;

    /// <summary>
    /// Read every file of <paramref name="lane"/> with <paramref name="read"/>, on this thread and up
    /// to the lane's bound of others, and return once every reader has ended.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static void Read(ReadingLane<T> lane, Action<T, CancellationToken> read, CancellationToken ct) =>
        new LaneReaders<T>(lane, read, ct).Run();

    private void Run()
    {
        var started = 0;
        while (started < _helpersWanted && _crew.TryStartHelper())
        {
            started++;
        }

        try
        {
            ReadWhileAny();
        }
        catch (Exception ex)
        {
            // Held, not thrown here: the helpers are still inside the caller's reads, and stop only
            // once they see it. It is thrown below, unchanged, when they have.
            _crew.Fail(ex);
        }

        _crew.Finish();
        _ct.ThrowIfCancellationRequested();
    }

    private void ReadWhileAny()
    {
        while (!Stopping)
        {
            var file = Interlocked.Increment(ref _next);
            if (file >= _files.Count)
            {
                return;
            }

            _read(_files[file], _ct);
        }
    }
}
