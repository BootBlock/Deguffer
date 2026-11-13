using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution;

/// <summary>
/// The work a planning pass starts at its top (<see cref="ICleanupProvider.BeginPass"/>), from the
/// moment each provider starts it to the moment the last of it has ended: the pass's token, the bound on
/// how many tool queries started that way run at once, and the tasks the pass waits for before it returns.
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
    /// <summary>
    /// How many tool queries the providers start at the top of a pass run at once.
    ///
    /// <para>Half the logical processors, because what the queries contend for is processor time while
    /// each interpreter loads, and a query waits on almost nothing else: about twenty start in a pass,
    /// and one machine measured 13.1 to 14.7 s of the tools' processor time in a whole warm pass.
    /// Never fewer than two, so one slow tool does not hold every other behind it, and never more than
    /// <see cref="CleanupPlanner.ProvidersAtOnce"/>, which already holds that many tools at once when
    /// each plan starts its own.</para>
    /// </summary>
    internal static readonly int ToolsAtOnce = Math.Clamp(Environment.ProcessorCount / 2, 2, CleanupPlanner.ProvidersAtOnce);

    private readonly CancellationTokenSource _pass;
    private readonly SemaphoreSlim _places;
    private readonly List<Task> _started = [];

    /// <param name="toolsAtOnce">How many queries started through <see cref="Ask"/> run at once.</param>
    /// <param name="ct">The caller's, which ends the pass's work when it is cancelled.</param>
    public PassStart(int toolsAtOnce, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toolsAtOnce);

        _places = new SemaphoreSlim(toolsAtOnce, toolsAtOnce);
        _pass = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Token = _pass.Token;
    }

    /// <summary>
    /// The pass's token, cancelled when the pass ends, fails or is cancelled.
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

    /// <summary>Has <paramref name="provider"/> start what its plan will wait on, and keeps it to wait for.</summary>
    internal void Begin(ICleanupProvider provider) => _started.Add(provider.BeginPass(this));

    /// <summary>
    /// Starts the planner's own <paramref name="work"/> for the pass under <see cref="Token"/>, off the
    /// planner's thread and without a place, and keeps it to wait for. For the reading of the registered
    /// storage, which every plan of the pass is held to: queued behind the tool queries, every plan would
    /// wait for them too. Asked on the planner's thread, as <see cref="Begin(ICleanupProvider)"/> is,
    /// because the tasks kept are not guarded for two threads at once.
    /// </summary>
    internal Task<T> Begin<T>(Func<CancellationToken, Task<T>> work)
    {
        var started = Task.Run(() => work(Token), Token);
        _started.Add(started);
        return started;
    }

    /// <summary>Tells everything started to stop, for a plan that failed and so ends the pass at once.</summary>
    internal Task CancelAsync() => _pass.CancelAsync();

    /// <summary>
    /// Stops everything started, and returns once each has ended, not only been told to: a tool ends
    /// after its cancellation is asked for, and the next pass may start its own the moment this one
    /// returns.
    ///
    /// <para>How each ended is not thrown here. A plan that awaited it reported it, a plan that asked
    /// after it failed asked again and reported its own answer, and a pass that ended before any plan
    /// asked reports why it ended, which is the answer its caller is owed.</para>
    /// </summary>
    internal async Task EndAsync()
    {
        await _pass.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_started).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        _places.Dispose();
        _pass.Dispose();
    }
}
