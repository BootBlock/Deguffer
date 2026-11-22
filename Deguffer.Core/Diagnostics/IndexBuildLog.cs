namespace Deguffer.Core.Diagnostics;

/// <summary>
/// What each volume's index build did in the work a caller ran while this log recorded: when it
/// started and when its index was ready, how long it read at low priority, and how many questions
/// about its volume the table and the walk answered. For the benchmark to report beside a planning
/// pass (#1081), so what a build yielding to the walks it races costs the pass is measured rather
/// than estimated from a build timed alone.
///
/// <para><b>Scoped to the asynchronous flow that opened it</b>, through <see cref="RecordIn"/>, as
/// <see cref="MachineCallTally"/> is and for its reason: passes run side by side in the tests. A build
/// keeps its volume's <see cref="IndexBuildTrace"/> from the flow that started it, because it reads
/// on a thread of its own and its priority changes on whichever thread starts or ends a walk.</para>
///
/// <para>Keyed by drive letter, and holding counts and times only, never a path, so a pasted report
/// names nothing on the machine.</para>
///
/// <para>Not a user setting and never on in the app: nothing opens a log but the benchmark and the
/// tests, and with none open, each place that records reads the flow's value once.</para>
/// </summary>
internal sealed class IndexBuildLog
{
    private static readonly AsyncLocal<IndexBuildLog?> Recording = new();

    private readonly TimeProvider _time;
    private readonly long _opened;
    private readonly Lock _gate = new();
    private readonly SortedDictionary<char, IndexBuildTrace> _volumes = [];

    /// <param name="time">What every time is read from, from the moment the log is made. The system clock by default.</param>
    public IndexBuildLog(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _opened = _time.GetTimestamp();
    }

    /// <summary>The log recording the current flow, or null where none is.</summary>
    internal static IndexBuildLog? Current => Recording.Value;

    /// <summary>Each volume met so far, in order of drive letter, as it stands now.</summary>
    public IReadOnlyList<IndexBuildTimes> Volumes
    {
        get
        {
            lock (_gate)
            {
                return [.. _volumes.Values.Select(trace => trace.Times())];
            }
        }
    }

    /// <summary>
    /// Record the builds started, and the questions answered, by the caller's flow and everything it
    /// starts in this log until the returned scope is disposed.
    /// </summary>
    public FlowScope<IndexBuildLog> RecordIn() => FlowScope<IndexBuildLog>.Enter(Recording, this);

    /// <summary>
    /// One question about <paramref name="driveLetter"/>, answered by the table where
    /// <paramref name="byTable"/> and by a walk otherwise, for the log recording the current flow.
    /// Nothing is recorded where no log is.
    /// </summary>
    internal static void Answered(char driveLetter, bool byTable) => Current?.Of(driveLetter).Answered(byTable);

    /// <summary>The trace of <paramref name="driveLetter"/>, begun the first time the volume is met.</summary>
    internal IndexBuildTrace Of(char driveLetter)
    {
        var key = char.ToUpperInvariant(driveLetter);

        lock (_gate)
        {
            if (!_volumes.TryGetValue(key, out var trace))
            {
                _volumes[key] = trace = new IndexBuildTrace(key, _time, _opened);
            }

            return trace;
        }
    }
}
