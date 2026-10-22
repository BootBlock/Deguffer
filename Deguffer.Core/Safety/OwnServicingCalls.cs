namespace Deguffer.Core.Safety;

/// <summary>
/// When Deguffer itself asked Windows' servicing stack something, so the stack it woke is not taken for
/// an update.
///
/// <para><b>Why a record is needed.</b> Windows starts <c>TrustedInstaller</c> and <c>TiWorker</c> for
/// any DISM call, a read-only analysis included, and keeps both running for minutes after the call
/// returns. They are also what an installing update runs, so the update hold reads them as one. The
/// component store's analysis runs immediately before its cleanup, which the hold then refused every
/// time: the cleanup never ran, and the store was offered again at the same size.</para>
///
/// <para><b>Attributed by when the process was created.</b> A servicing process created while one of
/// Deguffer's own calls was running was started for that call. One created before any call, or after
/// one returned, was started by something else and still holds. The stack an update starts in the
/// minutes after a call reuses the process the call woke, so it cannot be told apart from it. Windows'
/// servicing runs one session at a time, and DISM refuses to clean while a restart is owed, which the
/// hold asks separately, so that case is left to them. That covers a DISM command alone, so only the
/// component store's hold consults this record, and a removal by path is held by any servicing
/// stack.</para>
///
/// <para><b>The machine's own clock, never a <see cref="TimeProvider"/>.</b> Each instant is compared
/// with a creation time the kernel recorded, so both must come from the same clock.</para>
/// </summary>
public sealed class OwnServicingCalls
{
    /// <summary>The record every provider and run in this process shares, since the stack is shared.</summary>
    public static readonly OwnServicingCalls Current = new();

    /// <summary>
    /// How many calls are remembered. A servicing process outlives the call that woke it by minutes,
    /// not by dozens of calls, and forgetting one only holds more back.
    /// </summary>
    private const int MostRemembered = 32;

    private readonly Lock _gate = new();
    private readonly Queue<Call> _calls = new();

    /// <summary>
    /// Runs <paramref name="call"/>, which asks the servicing stack something, and remembers when it
    /// ran.
    /// </summary>
    public async Task<T> MakeAsync<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var made = new Call(DateTimeOffset.UtcNow);

        lock (_gate)
        {
            if (_calls.Count == MostRemembered)
            {
                _calls.Dequeue();
            }

            _calls.Enqueue(made);
        }

        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            made.End(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Whether a process created at <paramref name="startedAt"/> was created while one of Deguffer's
    /// own calls was running.
    /// </summary>
    public bool Started(DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            return _calls.Any(call => call.Spans(startedAt));
        }
    }

    /// <summary>One call, whose end is unknown while it runs.</summary>
    private sealed class Call(DateTimeOffset start)
    {
        private long _endTicks = long.MaxValue;

        public void End(DateTimeOffset at) => Interlocked.Exchange(ref _endTicks, at.UtcTicks);

        public bool Spans(DateTimeOffset at) =>
            at.UtcTicks >= start.UtcTicks && at.UtcTicks <= Interlocked.Read(ref _endTicks);
    }
}
