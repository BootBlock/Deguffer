using Deguffer.Core.Memory;

namespace Deguffer.Testing;

/// <summary>
/// Answers every read with <see cref="Snapshot"/>, counts the reads, and records how many were ever in
/// progress at once, so a consumer's cadence can be proven without this machine.
/// </summary>
public sealed class FakeMemorySource : IMemorySource
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _released = new(initialState: true);
    private int _holdFrom = int.MaxValue;
    private int _reads;
    private int _inProgress;
    private int _mostAtOnce;

    public static MemorySnapshot Snapshot { get; } = new(
        new SystemMemory(
            PhysicalTotal: 16L << 30,
            Available: 6L << 30,
            CommitCharge: 12L << 30,
            CommitLimit: 20L << 30,
            SystemCache: 5L << 30,
            PagedPool: 1L << 29,
            NonPagedPool: 1L << 28,
            Lists: null,
            ListState: MemoryListState.NotReturned),
        new ProcessMemoryTable([], ProcessFigures.Checked, Complete: true),
        new ServiceTable([], ServiceListing.Listed));

    public int Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads;
            }
        }
    }

    public int MostAtOnce
    {
        get
        {
            lock (_gate)
            {
                return _mostAtOnce;
            }
        }
    }

    /// <summary>Make read <paramref name="read"/> and every one after it wait for <see cref="Release"/>.</summary>
    public void HoldFrom(int read)
    {
        lock (_gate)
        {
            _holdFrom = read;
            _released.Reset();
        }
    }

    public void Release() => _released.Set();

    /// <summary>Completes once read <paramref name="read"/> has begun.</summary>
    public async Task WhenReadStartsAsync(int read, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;

        while (Reads < read)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Read {read} never began.");
            }

            await Task.Delay(5);
        }
    }

    public MemorySnapshot Read(CancellationToken ct)
    {
        bool hold;

        lock (_gate)
        {
            _reads++;
            _inProgress++;
            _mostAtOnce = Math.Max(_mostAtOnce, _inProgress);
            hold = _reads >= _holdFrom;
        }

        try
        {
            if (hold)
            {
                _released.Wait(ct);
            }

            return Snapshot;
        }
        finally
        {
            lock (_gate)
            {
                _inProgress--;
            }
        }
    }
}
