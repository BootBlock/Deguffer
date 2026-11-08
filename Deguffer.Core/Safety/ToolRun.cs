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
/// How Deguffer reads the text a tool writes to its standard output and error.
/// </summary>
public enum ToolOutputEncoding
{
    /// <summary>
    /// The console's output code page where Deguffer has a console, and UTF-8 where it has none, which
    /// is the app's case. This is how <see cref="System.Diagnostics.Process"/> read a redirected output,
    /// and every parser of tool output was written against it.
    /// </summary>
    ConsoleCodePage,

    /// <summary>
    /// UTF-8 whatever the console's code page, for a tool that writes UTF-8 to a pipe however the
    /// console is set. Read in a console's OEM code page such as 850, a non-ASCII path such a tool
    /// prints reaches its parser mangled, and matches nothing it is compared with.
    /// </summary>
    Utf8,
}

/// <summary>
/// How long a started tool may run, what a cancellation does to it, which environment it starts
/// with, and how its output is read. Every <see cref="IProcessRunner.RunAsync"/> names one, because each is a decision about that
/// tool rather than a property of the runner.
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
    /// Every variable the tool starts with, or null for this process's own, which is what a tool
    /// Deguffer starts is given unless a caller has a reason to ask otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; private init; }

    /// <summary>
    /// How the tool's output is read. <see cref="ToolOutputEncoding.ConsoleCodePage"/> unless the
    /// caller knows the tool writes something else (<see cref="WritingIn"/>).
    /// </summary>
    public ToolOutputEncoding Output { get; private init; }

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

    /// <summary>
    /// This run, started with <paramref name="environment"/> in place of this process's own variables:
    /// the environment a tool the user starts now would have (<see cref="IUserEnvironment.ChangedEnvironment"/>),
    /// for a query of where that tool keeps its folders.
    /// </summary>
    public ToolRun StartedWith(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return this with { Environment = environment };
    }

    /// <summary>This run, with the tool's output read as <paramref name="output"/>.</summary>
    public ToolRun WritingIn(ToolOutputEncoding output) => this with { Output = output };
}
