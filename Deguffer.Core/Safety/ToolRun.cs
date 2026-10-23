namespace Deguffer.Core.Safety;

/// <summary>
/// What becomes of a tool's command once it has started and the run is cancelled.
/// </summary>
public enum CancelledCommand
{
    /// <summary>
    /// The tool and every process it started are ended, and the run reports the cancellation. For a
    /// tool that can stop part-way without harm: what it had removed is gone, and what it had not is
    /// still there for the next clean.
    /// </summary>
    Ended,

    /// <summary>
    /// The tool is left to finish, and its output is read until it exits. For a tool whose half-done
    /// work is damage rather than a smaller result: DISM ended mid-servicing risks the component
    /// store. Never ended by Deguffer at all, so closing Deguffer does not end it either.
    /// </summary>
    Finishes,
}

/// <summary>
/// How long a started tool may run, and what a cancellation does to it. Every
/// <see cref="IProcessRunner.RunAsync"/> names one, because a tool's lifetime is a decision about
/// that tool rather than a property of the runner.
///
/// <para>A tool that may be ended runs in a job object Deguffer holds open for its whole life and
/// closes only by exiting, so neither it nor anything it starts outlives Deguffer. One that
/// <see cref="CancelledCommand.Finishes"/> is outside it, for the reason it is not ended on cancel.</para>
/// </summary>
public sealed record ToolRun
{
    /// <summary>
    /// How long a query is given by default. A query only reads, and the slowest the providers ask
    /// (<c>conda clean --dry-run</c> over a large package cache, <c>conda info</c> on a cold start)
    /// answers in well under a minute. Long enough for a machine under load, and short enough that a
    /// tool that hangs does not hold the scan until the user notices.
    /// </summary>
    public static readonly TimeSpan QueryLimit = TimeSpan.FromMinutes(2);

    private ToolRun(CancelledCommand whenCancelled, TimeSpan? limit)
    {
        WhenCancelled = whenCancelled;
        Limit = limit;
    }

    /// <summary>A read-only query, ended at <see cref="QueryLimit"/> or when the run is cancelled.</summary>
    public static ToolRun Query { get; } = new(CancelledCommand.Ended, QueryLimit);

    public CancelledCommand WhenCancelled { get; }

    /// <summary>
    /// How long the tool may run before it is ended and the run fails, or null for no limit. Only a
    /// tool that is <see cref="CancelledCommand.Ended"/> has one: a limit on one that
    /// <see cref="CancelledCommand.Finishes"/> could not be enforced without the harm it exists to avoid.
    /// </summary>
    public TimeSpan? Limit { get; }

    /// <summary>A read-only query that is known to take longer than <see cref="QueryLimit"/>.</summary>
    public static ToolRun QueryWithin(TimeSpan limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        return new ToolRun(CancelledCommand.Ended, limit);
    }

    /// <summary>
    /// A command that changes something, with no time limit: a clean of a large cache takes as long as
    /// it takes, and the user's Cancel is what bounds it.
    /// </summary>
    public static ToolRun Command(CancelledCommand whenCancelled) => new(whenCancelled, limit: null);
}
