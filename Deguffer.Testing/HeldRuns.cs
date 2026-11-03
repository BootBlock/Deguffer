namespace Deguffer.Testing;

/// <summary>
/// Tool runs a <see cref="FakeProcessRunner"/> keeps open until <see cref="Together"/> of them have
/// started, so a test proves that code starts its queries at once by the order things happened in,
/// never by how long they took. Run one after another, the first held run waits for a second that
/// cannot start until it ends, and the hold gives up at the deadline.
///
/// <para>Once the set has started, every held run is released, and any run that arrives after that
/// replies at once. <see cref="MostAtOnce"/> counts every run the hold matched, from its start to its
/// reply, so a test can assert an upper bound as well.</para>
/// </summary>
public sealed class HeldRuns
{
    /// <summary>Far longer than a few runs take to start together, and far shorter than a hung test.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _deadline;
    private int _started;
    private int _running;
    private int _mostAtOnce;

    public HeldRuns(int together, TimeSpan? deadline = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(together, 1);

        Together = together;
        _deadline = deadline ?? DefaultDeadline;
    }

    /// <summary>How many runs must have started before any is released.</summary>
    public int Together { get; }

    /// <summary>Every run the hold matched, released or not.</summary>
    public int Started => Volatile.Read(ref _started);

    /// <summary>True once <see cref="Together"/> runs had started before any of them replied.</summary>
    public bool StartedTogether => _allStarted.Task.IsCompletedSuccessfully;

    /// <summary>The most matched runs that were open at the same moment.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>
    /// Counts a run as started and waits until the whole set has. A run still waiting at the deadline
    /// throws, naming how many of the set started, so a test that runs its queries one at a time fails
    /// rather than hangs. The run's token is not watched here: the runner checks it once the run is
    /// released, as a real tool's reply arrives after a cancellation.
    /// </summary>
    internal async Task EnterAsync()
    {
        var running = Interlocked.Increment(ref _running);
        var seen = Volatile.Read(ref _mostAtOnce);

        while (running > seen && Interlocked.CompareExchange(ref _mostAtOnce, running, seen) is var was && was != seen)
        {
            seen = was;
        }

        if (Interlocked.Increment(ref _started) >= Together)
        {
            _allStarted.TrySetResult();
        }

        try
        {
            await _allStarted.Task.WaitAsync(_deadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Decrement(ref _running);
            throw new TimeoutException(
                $"Only {Started} of {Together} held runs started within {_deadline.TotalSeconds:0} s, so they were run one after another.");
        }
    }

    /// <summary>Counts a released run as having replied.</summary>
    internal void Leave() => Interlocked.Decrement(ref _running);
}
