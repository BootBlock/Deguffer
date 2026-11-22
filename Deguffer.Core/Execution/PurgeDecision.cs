namespace Deguffer.Core.Execution;

/// <summary>
/// Whether a Disk Cleanup handler that has measured may go on to purge: decided once, by the step's
/// look on the disk, and waited for on the handler's own thread.
///
/// <para><b>A blocking wait, not a task.</b> The handler is purged from the apartment it was measured
/// in, on a thread of its own that has nothing else to do, so that thread blocks until the look
/// decides.</para>
/// </summary>
public sealed class PurgeDecision : IDisposable
{
    private readonly ManualResetEventSlim _decided = new();
    private bool _mayPurge;

    /// <summary>Lets the handler purge, or lets it go without purging.</summary>
    /// <exception cref="InvalidOperationException">The decision was already made.</exception>
    public void Decide(bool mayPurge)
    {
        if (_decided.IsSet)
        {
            throw new InvalidOperationException("Whether to purge was already decided.");
        }

        // Written before the event is set, which publishes it to the waiting thread.
        _mayPurge = mayPurge;
        _decided.Set();
    }

    /// <summary>Blocks until the decision is made, and answers whether the handler may purge.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled first.</exception>
    public bool Wait(CancellationToken ct)
    {
        _decided.Wait(ct);
        return _mayPurge;
    }

    public void Dispose() => _decided.Dispose();
}
