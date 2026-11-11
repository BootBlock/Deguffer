namespace Deguffer.Core.Execution;

/// <summary>
/// A Disk Cleanup handler asked what it would clear before the plan knows it needs the answer, so the
/// handler's measurement runs beside whatever the plan has to read first. The plan then either takes
/// the answer or discards the question.
///
/// <para><b>A stopped survey's answer is never read.</b> A handler told to stop returns
/// <c>E_ABORT</c> from its measurement, which <see cref="DiskCleanupHandlers.Survey"/> reads as "has
/// something", for the reason it gives for any failure to measure. So discarding stops the handler,
/// waits for it to return, and drops what it said, and taking refuses an answer given after the pass
/// was cancelled.</para>
///
/// <para><b>Discarding waits for the handler to return.</b> Nothing on the handler's side is
/// documented beyond the interface, so no handler is left running past the plan that asked, where the
/// clean could start a second instance beside it. Measured elevated on Windows 11 24H2, the
/// <em>Device Driver Packages</em> handler returned within 110 ms of being told to stop.</para>
///
/// <para>The first of <see cref="TakeAsync"/>, <see cref="DiscardAsync"/> and <see cref="Abandon"/>
/// to be called decides, so a plan can discard in a <c>finally</c> whether or not it took the
/// answer.</para>
/// </summary>
internal sealed class EarlyDiskCleanupSurvey
{
    private readonly CancellationTokenSource _stop;
    private readonly Task<DiskCleanupSurvey> _answer;
    private int _claimed;

    /// <summary>Starts asking <paramref name="handler"/> about <paramref name="volume"/>.</summary>
    /// <param name="ct">The pass's token: cancelling the pass stops the handler too.</param>
    public EarlyDiskCleanupSurvey(
        IDiskCleanupHandlers handlers, string handler, string volume, DiskCleanupQuestion question, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stop = _stop.Token;

        // On its own thread, because the handler measures synchronously and can take seconds.
        _answer = Task.Run(() => handlers.Survey(handler, volume, question, stop), stop);
    }

    /// <summary>The handler's answer, once it has given one.</summary>
    /// <exception cref="OperationCanceledException">The pass was cancelled while the handler measured.</exception>
    /// <exception cref="InvalidOperationException">The question was already taken, discarded or abandoned.</exception>
    public async Task<DiskCleanupSurvey> TakeAsync()
    {
        if (!Claim())
        {
            throw new InvalidOperationException("This survey was already taken, discarded or abandoned.");
        }

        try
        {
            var answer = await _answer.ConfigureAwait(false);
            _stop.Token.ThrowIfCancellationRequested();
            return answer;
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Stops the handler where it is still measuring, and waits for it to return. Does nothing where
    /// the answer was already taken.
    ///
    /// <para><b>What the handler threw is not the plan's failure.</b> The plan discards the question
    /// because it does not need the answer, and that plan never asked before the survey started
    /// early, so a handler that fails to load or to measure must not replace it. The clean that runs
    /// the handler meets the same failure, where it is the clean's to report.</para>
    /// </summary>
    public async Task DiscardAsync()
    {
        if (!Claim())
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await ((Task)_answer).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Stops the handler without waiting for it, where the pass that asked has ended without a plan
    /// to take the answer or discard the question.
    /// </summary>
    public void Abandon()
    {
        if (Claim())
        {
            _stop.Cancel();
        }
    }

    private bool Claim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}
