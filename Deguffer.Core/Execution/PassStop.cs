namespace Deguffer.Core.Execution;

/// <summary>
/// How a planning pass is ended before its rows have all planned: by its caller, or by a failure.
///
/// <para><b>Two tokens, because a failure is reported by whoever owns the loop it happened in.</b> A
/// failure in a row's slot ends the work started at the top of the pass, and the slots' loop stops its
/// other slots itself and keeps that failure. Cancelling the loop's own token as well would lose it: a
/// loop stopped by its token says only that it was stopped. A failure outside the slots, in the reading of
/// the registered storage or while a row's plan is held to it, has no loop to keep it, so it stops the
/// slots too and <see cref="PlanningPass"/> throws it itself.</para>
/// </summary>
internal sealed class PassStop : IDisposable
{
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _slots;

    public PassStop(CancellationToken ct)
    {
        _work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _slots = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>Ends what the pass started at its top, the reading of the registered storage included.</summary>
    public CancellationToken Work => _work.Token;

    /// <summary>Ends the slots the rows plan in.</summary>
    public CancellationToken Slots => _slots.Token;

    /// <summary>Whether the pass's work has been ended, by its caller or by a failure.</summary>
    public bool IsEnding => _work.IsCancellationRequested;

    /// <summary>For a failure in a row's slot, and for the pass's end however it ends.</summary>
    public Task EndWorkAsync() => _work.CancelAsync();

    /// <summary>For a failure outside the slots.</summary>
    public async Task EndAllAsync()
    {
        await _work.CancelAsync().ConfigureAwait(false);
        await _slots.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _work.Dispose();
        _slots.Dispose();
    }
}
