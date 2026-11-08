namespace Deguffer.Testing;

/// <summary>
/// Units of work counted while they are under way, with the first held until a second has begun, so
/// a test proves that code runs work side by side by the order things happened in, never by whether a
/// second thread happened to start inside a timed hold. A loaded machine can start that thread late
/// with nothing wrong, and a timed hold alone then sees one unit at a time.
///
/// <para>Run one at a time, the first unit waits for a second that cannot begin until it ends, and
/// the wait gives up after the patience, so <see cref="MostAtOnce"/> stays at one and the test fails
/// rather than hangs. Units after the first are never held here, so a test asserts its upper bound on
/// <see cref="MostAtOnce"/> as well.</para>
/// </summary>
/// <param name="patience">
/// How long the first unit waits for a second. Ten seconds by default: far longer than a loaded
/// machine takes to start a second thread, and far shorter than a hung test.
/// </param>
public sealed class HeldOverlap(TimeSpan? patience = null)
{
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _secondBegun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _patience = patience ?? DefaultPatience;
    private int _begun;
    private int _running;
    private int _mostAtOnce;

    /// <summary>The most units that were under way at the same moment.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>
    /// Counts a unit as under way. The first unit to begin waits here until a second has begun, or
    /// until the patience runs out. It is counted before it waits, so the second sees it under way.
    /// </summary>
    public void Begin()
    {
        var running = Interlocked.Increment(ref _running);
        for (var most = Volatile.Read(ref _mostAtOnce); running > most; most = Volatile.Read(ref _mostAtOnce))
        {
            Interlocked.CompareExchange(ref _mostAtOnce, running, most);
        }

        if (Interlocked.Increment(ref _begun) > 1)
        {
            _secondBegun.TrySetResult();
        }
        else
        {
            // The outcome is not read: a unit left alone at the deadline shows as MostAtOnce of one.
            _secondBegun.Task.Wait(_patience);
        }
    }

    /// <summary>Counts a unit as finished.</summary>
    public void End() => Interlocked.Decrement(ref _running);
}
