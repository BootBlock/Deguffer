namespace Deguffer.Core.Diagnostics;

/// <summary>
/// How many times each <see cref="MachineCall"/> was made by the work a caller ran while this tally
/// counted, and how long those calls took: for the benchmark to report beside a planning pass's own
/// timing, so an estimate of where a pass spends its time becomes a measurement (#516).
///
/// <para><b>Scoped to the asynchronous flow that opened it</b>, through <see cref="CountIn"/>, rather
/// than kept in a static count. The planner runs providers in parallel and the tests run passes side
/// by side, so a shared count would add one pass's calls to another's. Every task, parallel slot and
/// worker thread a pass starts runs in that flow, so its calls are counted wherever it makes them.
/// Work that started before the tally opened, or outside the caller's flow, is not counted.</para>
///
/// <para><b>Counted where Core hands the question to the machine</b>: the delegate or seam each
/// primitive is reached through, so a test standing in for Windows is counted as Windows would be.
/// Counts and durations only, never a path, so a pasted report names nothing on the machine.</para>
///
/// <para>Not a user setting and never on in the app: nothing opens a tally but the benchmark and the
/// tests, and with none open, timing a call costs one read of the flow's value.</para>
/// </summary>
public sealed class MachineCallTally
{
    private static readonly AsyncLocal<MachineCallTally?> Counting = new();

    private static readonly int Kinds = Enum.GetValues<MachineCall>().Length;

    private readonly TimeProvider _time;
    private readonly int[] _calls = new int[Kinds];
    private readonly long[] _elapsedTicks = new long[Kinds];

    /// <param name="time">What each call is timed by. The system clock by default.</param>
    public MachineCallTally(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Count the calls made by the caller's flow, and by everything it starts, in this tally until the
    /// returned scope is disposed, which gives the flow back to whatever tally counted it before.
    /// </summary>
    public MachineCallScope CountIn()
    {
        var outer = Counting.Value;
        Counting.Value = this;

        return new MachineCallScope(outer);
    }

    /// <summary>What this tally has counted of <paramref name="call"/> so far.</summary>
    public MachineCallCount Of(MachineCall call) =>
        new(Volatile.Read(ref _calls[(int)call]), TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks[(int)call])));

    /// <summary>
    /// Start timing one <paramref name="call"/> for the tally counting the current flow, ended when the
    /// returned timing is disposed. Nothing is counted where no tally is.
    /// </summary>
    internal static MachineCallTiming Time(MachineCall call) =>
        Counting.Value is { } tally ? new MachineCallTiming(tally, call, tally._time.GetTimestamp()) : default;

    /// <summary>Put the flow back in the hands of <paramref name="tally"/>, for <see cref="MachineCallScope"/>.</summary>
    internal static void Restore(MachineCallTally? tally) => Counting.Value = tally;

    /// <summary>Count one <paramref name="call"/> that began at <paramref name="started"/>, for <see cref="MachineCallTiming"/>.</summary>
    internal void Ended(MachineCall call, long started)
    {
        Interlocked.Increment(ref _calls[(int)call]);
        Interlocked.Add(ref _elapsedTicks[(int)call], _time.GetElapsedTime(started).Ticks);
    }
}
