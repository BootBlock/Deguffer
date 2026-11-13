namespace Deguffer.Core.Execution;

/// <summary>
/// What a planning pass hands each provider as it begins (<see cref="Providers.ICleanupProvider.BeginPass"/>):
/// the pass's token, and a bound on how many tool queries started that way run at once.
///
/// <para><b>Why a bound.</b> Most of what the providers start early is a package manager reporting one
/// setting, and most of those are interpreters (npm and pnpm are Node.js, pip, conda and Poetry are
/// Python) whose start costs processor time while they load. Started together, some twenty would start
/// in the same instant, and on a machine with few processors each would take several times as long as
/// it does alone, which is the wait starting early exists to hide. On one machine even with tool starts
/// spread over the planning slots, each <c>arduino-cli config get</c> inside a pass took about 117 ms
/// against 55 ms alone.</para>
///
/// <para><b>Only a query started early waits for a place.</b> A plan that asks for the same answer while
/// the early query still waits starts it itself, in its own slot, rather than waiting behind the queue.
/// The answer is remembered for the pass, so the early query then finds it already asked and starts
/// nothing.</para>
/// </summary>
public sealed class PassStart : IDisposable
{
    private readonly SemaphoreSlim _places;

    /// <param name="toolsAtOnce">How many queries started through <see cref="Ask"/> run at once.</param>
    /// <param name="token">The pass's, which the planner cancels when the pass ends, fails or is cancelled.</param>
    public PassStart(int toolsAtOnce, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toolsAtOnce);

        _places = new SemaphoreSlim(toolsAtOnce, toolsAtOnce);
        Token = token;
    }

    /// <summary>
    /// The pass's token, for work started early that takes no place: one long answer whose wait is not
    /// spent starting an interpreter, and which would lengthen the pass if it queued behind the rest.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Starts <paramref name="query"/> under <see cref="Token"/> once a place is free, and keeps the place
    /// until the work it returns has ended. Returns without waiting for either.
    /// </summary>
    /// <param name="query">
    /// The provider's check and its question, run off the planner's thread: the check before a question
    /// (a search of <c>PATH</c>, a probe of a folder) is disk work, and made on the planner's thread it
    /// would hold up every provider behind it before any of them had started.
    /// </param>
    /// <returns>The work, ended once the query's has, or once the pass ended while it waited for a place.</returns>
    public async Task Ask(Func<CancellationToken, Task> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _places.WaitAsync(Token).ConfigureAwait(false);

        try
        {
            await Task.Run(() => query(Token), Token).ConfigureAwait(false);
        }
        finally
        {
            _places.Release();
        }
    }

    /// <summary>
    /// Starts <paramref name="query"/> under <see cref="Token"/> at once, off the planner's thread, and
    /// without a place. Returns without waiting for it.
    ///
    /// <para>For a question whose wait is spent on another part of Windows rather than on an
    /// interpreter loading: a service, a driver store or an engine that does the work while the asker
    /// sleeps. It adds almost nothing to what the bound shares out, and queued behind every package
    /// manager's query it would start only once they had ended.</para>
    /// </summary>
    /// <param name="query">The provider's check and its question, run off the planner's thread for the reason <see cref="Ask"/> gives.</param>
    /// <returns>The work, ended once the query's has.</returns>
    public Task AskAtOnce(Func<CancellationToken, Task> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.Run(() => query(Token), Token);
    }

    public void Dispose() => _places.Dispose();
}
