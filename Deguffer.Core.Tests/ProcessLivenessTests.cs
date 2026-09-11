using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a record naming a process id and a creation time still describes a running process.
///
/// <para>This is where the three answers lead somewhere different. The dangerous direction is
/// <see cref="ProcessState.NotRunning"/>, because that is the answer that lets a file naming the
/// process be removed. Every case that could not establish the process has ended must therefore
/// land anywhere else.</para>
/// </summary>
public class ProcessLivenessTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The holder created at exactly the recorded time is the recorded process.
    ///
    /// <para>The same instant expressed in another offset is still the same instant.
    /// <c>DateTimeOffset.FromFileTime</c> hands back local time, which is how a caller parsing a
    /// FILETIME would build it, and a comparison of clock readings would call that a different
    /// process.</para>
    /// </summary>
    [Fact]
    public void TheHolderCreatedAtTheRecordedTimeIsTheRecordedProcess()
    {
        var liveness = ProcessLiveness.Running(Recorded);

        Assert.Equal(ProcessState.Running, liveness.StateOfProcessStartedAt(Recorded));
        Assert.Equal(ProcessState.Running, liveness.StateOfProcessStartedAt(Recorded.ToOffset(TimeSpan.FromHours(1))));
    }

    /// <summary>
    /// An id held by a process created later has been reused, so the recorded process has ended. One
    /// tick later is enough, because a creation time is exact.
    /// </summary>
    [Fact]
    public void AnIdHeldByALaterProcessMeansTheRecordedOneEnded() =>
        Assert.Equal(
            ProcessState.NotRunning,
            ProcessLiveness.Running(Recorded.AddTicks(1)).StateOfProcessStartedAt(Recorded));

    [Fact]
    public void AFreeIdMeansTheRecordedProcessEnded() =>
        Assert.Equal(ProcessState.NotRunning, ProcessLiveness.NotRunning.StateOfProcessStartedAt(Recorded));

    /// <summary>
    /// A running holder whose creation time could not be read neither confirms the record nor
    /// rejects it. This is every process an unelevated account may not open, and a missing time
    /// treated as a mismatch would read each of them as a recycled id.
    /// </summary>
    [Fact]
    public void AHolderWhoseCreationTimeCouldNotBeReadIsUndetermined() =>
        Assert.Equal(ProcessState.Undetermined, ProcessLiveness.Running(null).StateOfProcessStartedAt(Recorded));

    /// <summary>
    /// A holder created before the recorded time says the record is not a creation time at all, since
    /// no process takes over an id before its predecessor existed. The time a process wrote once it
    /// was running is later than its creation, and it must not read as a recycled id.
    /// </summary>
    [Fact]
    public void AHolderCreatedBeforeTheRecordedTimeIsUndetermined() =>
        Assert.Equal(
            ProcessState.Undetermined,
            ProcessLiveness.Running(Recorded.AddMilliseconds(-489)).StateOfProcessStartedAt(Recorded));

    [Fact]
    public void NothingEstablishedAboutTheIdStaysUndetermined() =>
        Assert.Equal(ProcessState.Undetermined, ProcessLiveness.Undetermined.StateOfProcessStartedAt(Recorded));

    /// <summary>A state nobody set reads as the answer that refuses, not as a process that ended.</summary>
    [Fact]
    public void TheDefaultStateIsUndetermined() =>
        Assert.Equal(ProcessState.Undetermined, default(ProcessState));
}
