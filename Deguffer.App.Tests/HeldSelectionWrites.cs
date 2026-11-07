using Deguffer.Core.Configuration;

namespace Deguffer.App.Tests;

/// <summary>
/// Remembers nothing at first, and holds each write until the test lets it, counting the writes and
/// keeping the last one finished. The remembered ticks are written on a worker, and what a tick, a
/// burst of ticks and a flush do while a write is still going is the order this exists to arrange.
/// </summary>
internal sealed class HeldSelectionWrites : ISelectionStore, IDisposable
{
    private readonly ManualResetEventSlim _released = new(initialState: false);
    private readonly SemaphoreSlim _started = new(initialCount: 0);
    private int _writes;
    private IReadOnlyDictionary<string, RememberedSelection>? _written;

    /// <summary>Faults the first write, once it is released, as a store fault nothing expects would.</summary>
    public bool FaultFirstWrite { get; init; }

    /// <summary>How many writes have started.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>What the last write to finish was handed, or null before one has.</summary>
    public IReadOnlyDictionary<string, RememberedSelection>? Written => Volatile.Read(ref _written);

    /// <summary>Let the write under way finish, and every write after it.</summary>
    public void Release() => _released.Set();

    /// <summary>Wait for the next write to start, bounded so a write that never starts fails the test.</summary>
    public async Task WriteStartedAsync()
    {
        if (!await _started.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("No write of the remembered ticks started.");
        }
    }

    public IReadOnlyDictionary<string, RememberedSelection> Load() => new Dictionary<string, RememberedSelection>();

    public bool Save(IReadOnlyDictionary<string, RememberedSelection> selections)
    {
        var write = Interlocked.Increment(ref _writes);
        _started.Release();

        // Bounded, so a writer the test never releases fails its test rather than hanging it.
        if (!_released.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The test never released the write of the remembered ticks.");
        }

        if (write == 1 && FaultFirstWrite)
        {
            throw new InvalidOperationException("The test faulted the first write.");
        }

        Volatile.Write(ref _written, selections);

        return true;
    }

    public void Dispose()
    {
        _released.Dispose();
        _started.Dispose();
    }
}
