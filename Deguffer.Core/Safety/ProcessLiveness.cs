namespace Deguffer.Core.Safety;

/// <summary>What could be established about the process holding one id.</summary>
public enum ProcessState
{
    /// <summary>
    /// Nothing could be established. It is not "not running": a caller that folds the two together
    /// deletes a file whose process it merely failed to ask about.
    ///
    /// <para>The zero value on purpose, so a state nobody set reads as the answer that refuses.</para>
    /// </summary>
    Undetermined,

    /// <summary>
    /// A process holds the id. On its own this does not say which process — see
    /// <see cref="ProcessLiveness.StateOfProcessStartedAt"/>.
    /// </summary>
    Running,

    /// <summary>No running process holds the id.</summary>
    NotRunning,
}

/// <summary>
/// Whether the process with one id is running, and when it started — the answer a file needs when
/// it records a process id rather than a process name.
///
/// <para>A three-valued state rather than a boolean, for the reason <see cref="LiveTreeFindings.Complete"/>
/// gives: "nothing is running" and "we could not tell" lead to opposite decisions about a file whose
/// removal breaks somebody's live session.</para>
/// </summary>
public sealed record ProcessLiveness
{
    public static readonly ProcessLiveness NotRunning = new(ProcessState.NotRunning, null);

    public static readonly ProcessLiveness Undetermined = new(ProcessState.Undetermined, null);

    private ProcessLiveness(ProcessState state, DateTimeOffset? startedAt)
    {
        State = state;
        StartedAt = startedAt;
    }

    /// <param name="startedAt">
    /// When the process holding the id was created, or null where it could not be read.
    /// </param>
    public static ProcessLiveness Running(DateTimeOffset? startedAt) => new(ProcessState.Running, startedAt);

    public ProcessState State { get; }

    /// <summary>
    /// When the process now holding the id was created, as the kernel records it: exact to the
    /// 100-nanosecond tick, in UTC.
    ///
    /// <para><b>Null means not known, never "no match".</b> It is null whenever
    /// <see cref="State"/> is not <see cref="ProcessState.Running"/>, and also for a running process
    /// this account may not open, which is every service and every other account's process on an
    /// unelevated run. Comparing a recorded time against it directly treats that null as a
    /// mismatch, and a mismatch reads as the recorded process having ended — so ask
    /// <see cref="StateOfProcessStartedAt"/> instead.</para>
    /// </summary>
    public DateTimeOffset? StartedAt { get; }

    /// <summary>
    /// Whether the process that was created at <paramref name="startedAt"/> is the one holding this
    /// id: the question a record naming both an id and a creation time asks.
    ///
    /// <para><b>An id alone is not an identity.</b> Windows reuses process ids, so
    /// <see cref="ProcessState.Running"/> by itself says only that <em>some</em> process holds the id
    /// now. A record whose process ended and whose id passed to an unrelated one would otherwise be
    /// taken as live for as long as the stranger runs.</para>
    ///
    /// <list type="bullet">
    /// <item><see cref="ProcessState.Running"/> when the holder was created at exactly that time.</item>
    /// <item><see cref="ProcessState.NotRunning"/> when the id is free, or held by a process created
    /// later: an id passes to a new process only after the old one has gone.</item>
    /// <item><see cref="ProcessState.Undetermined"/> when nothing was established, when the holder's
    /// creation time could not be read, or when the holder was created <em>earlier</em> than the
    /// record says. No successor predates the process it replaced, so that record is not the id's
    /// creation time, and nothing follows from it.</item>
    /// </list>
    ///
    /// <para><b>Exact to the tick, so pass the creation time as Windows reports it.</b> Claude Code's
    /// session registry records it that way — its <c>procStart</c> is a FILETIME, observed equal to
    /// <c>GetProcessTimes</c> for the same process. A value rounded down to milliseconds or seconds
    /// sits just before the real one and reads as a later process, which is
    /// <see cref="ProcessState.NotRunning"/> and the direction that deletes. A time the process wrote
    /// once it was already running — the same registry's <c>startedAt</c>, observed 489 milliseconds
    /// after creation — is later than the real one and reads as
    /// <see cref="ProcessState.Undetermined"/>.</para>
    /// </summary>
    public ProcessState StateOfProcessStartedAt(DateTimeOffset startedAt) =>
        State is not ProcessState.Running ? State
        : StartedAt is not { } actual ? ProcessState.Undetermined
        : actual == startedAt ? ProcessState.Running
        : actual > startedAt ? ProcessState.NotRunning
        : ProcessState.Undetermined;
}
