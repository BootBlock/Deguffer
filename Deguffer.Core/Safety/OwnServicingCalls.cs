using System.Runtime.CompilerServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// When Deguffer itself asked Windows' servicing stack something, so the stack it woke, and the restart
/// key the stack wrote for it, are not taken for an update.
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
/// <para><b>The restart key, by when it was written.</b> For the length of a DISM session the servicing
/// stack also creates <see cref="RestartKey.ServicingRebootPending"/>, which is one of the keys that
/// say a restart is owed, and removes it when the session ends. Measured beside an elevated analysis,
/// it was present in every sample while DISM ran and in none after it exited. A key carries no owner,
/// so it is Deguffer's own only where it was last written while one of Deguffer's calls ran, and that
/// call began with no restart owed. Windows runs one servicing session at a time, so nothing else
/// wrote it then, and the reading at the call's start rules out an update's key that the session
/// touched. Every other restart key, and this one written at any other time or at an unknown time,
/// still holds.</para>
///
/// <para><b>The machine's own clock, never a <see cref="TimeProvider"/>.</b> Each instant is compared
/// with a time the kernel recorded, a process's creation or a key's last write, so both must come
/// from the same clock.</para>
/// </summary>
/// <param name="servicing">Asked as each call begins whether a restart is already owed.</param>
public sealed class OwnServicingCalls(IWindowsServicing servicing)
{
    private static readonly ConditionalWeakTable<IWindowsServicing, OwnServicingCalls> ByMachine = new();

    /// <summary>The record every provider and run in this process shares, since the stack is shared.</summary>
    public static OwnServicingCalls Current => For(WindowsServicing.Current);

    /// <summary>
    /// How many calls are remembered. A servicing process outlives the call that woke it by minutes,
    /// not by dozens of calls, and forgetting one only holds more back.
    /// </summary>
    private const int MostRemembered = 32;

    private readonly Lock _gate = new();
    private readonly Queue<Call> _calls = new();

    /// <summary>
    /// The one record of the machine <paramref name="servicing"/> describes, which every provider built
    /// on it shares: <see cref="Current"/> for this machine, and one of its own for a test's fake.
    /// </summary>
    public static OwnServicingCalls For(IWindowsServicing servicing)
    {
        ArgumentNullException.ThrowIfNull(servicing);

        return ByMachine.GetValue(servicing, machine => new OwnServicingCalls(machine));
    }

    /// <summary>
    /// Runs <paramref name="call"/>, which asks the servicing stack something, and remembers when it
    /// ran and whether a restart was owed as it began.
    /// </summary>
    public async Task<T> MakeAsync<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        // The start is taken before the restart is read, so a key written between the two is either
        // before the call's span or seen as owed at its start, and is never Deguffer's own.
        var start = DateTimeOffset.UtcNow;
        var made = new Call(start, beganOwed: servicing.ReadRestart().IsPending);

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

    /// <summary>
    /// Whether <paramref name="mark"/> is the servicing stack's session key for one of Deguffer's own
    /// calls: <see cref="RestartKey.ServicingRebootPending"/>, last written while a call ran that began
    /// with no restart owed.
    /// </summary>
    public bool Wrote(RestartMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        if (mark is not { Key: RestartKey.ServicingRebootPending, Written: { } written })
        {
            return false;
        }

        lock (_gate)
        {
            return _calls.Any(call => !call.BeganOwed && call.Spans(written));
        }
    }

    /// <summary>One call, whose end is unknown while it runs.</summary>
    /// <param name="start">When it began.</param>
    /// <param name="beganOwed">Whether a restart was already owed as it began.</param>
    private sealed class Call(DateTimeOffset start, bool beganOwed)
    {
        private long _endTicks = long.MaxValue;

        public bool BeganOwed => beganOwed;

        public void End(DateTimeOffset at) => Interlocked.Exchange(ref _endTicks, at.UtcTicks);

        public bool Spans(DateTimeOffset at) =>
            at.UtcTicks >= start.UtcTicks && at.UtcTicks <= Interlocked.Read(ref _endTicks);
    }
}
