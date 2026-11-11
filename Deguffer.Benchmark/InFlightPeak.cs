namespace Deguffer.Benchmark;

/// <summary>
/// Calls counted while they are in flight, so a run says how many it really made at once rather than
/// only how many it was allowed to.
/// </summary>
internal sealed class InFlightPeak
{
    private int _inFlight;
    private int _peak;

    /// <summary>The most calls that were in flight at once.</summary>
    public int Peak => Volatile.Read(ref _peak);

    /// <summary>One call in flight until the returned scope is disposed.</summary>
    public Scope Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        var peak = Volatile.Read(ref _peak);

        while (now > peak)
        {
            var was = Interlocked.CompareExchange(ref _peak, now, peak);

            if (was == peak)
            {
                break;
            }

            peak = was;
        }

        return new Scope(this);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly InFlightPeak _owner;

        internal Scope(InFlightPeak owner) => _owner = owner;

        public void Dispose() => Interlocked.Decrement(ref _owner._inFlight);
    }
}
