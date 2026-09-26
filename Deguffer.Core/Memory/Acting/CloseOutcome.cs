using System.ComponentModel;

namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What a close's report says once the press is over: the close itself where one went ahead, and a
/// sentence where nothing was sent (§7.2.1).
///
/// <para>Here rather than in the page for the reason <see cref="CloseReport"/> is: which of a
/// declined confirmation, a second-decision refusal and a close the user is told about decides what
/// they believe happened to another program's unsaved work, and a sentence that exists only inside a
/// page is one nothing can hold Deguffer to.</para>
/// </summary>
public sealed record CloseOutcome
{
    private CloseOutcome(CloseReport? report, string statement)
    {
        Report = report;
        Statement = statement;
    }

    /// <summary>The close, with its §5.6 evidence, or null where nothing was sent.</summary>
    public CloseReport? Report { get; }

    /// <summary>What the report says, whether or not anything was sent.</summary>
    public string Statement { get; }

    /// <summary>What came of one press that reached the confirmation.</summary>
    /// <param name="target">The program the user picked, as the snapshot they picked it from describes it.</param>
    /// <param name="attempt">What the close came to, or null where the user declined the confirmation.</param>
    public static CloseOutcome Of(ProcessMemory target, CloseAttempt? attempt)
    {
        ArgumentNullException.ThrowIfNull(target);

        return attempt switch
        {
            // Said rather than left unsaid: the previous sentence left standing reads, to somebody who
            // has just dismissed a dialog, as the outcome of it.
            null => new(null, $"{target.Named} was not asked to close. Nothing was sent."),

            { Report: { } done } => new(done, done.Statement),

            // The second decision, made with the handle held. The machine moved between the pick and
            // the confirmation, so what the page said then is out of date and this is the answer.
            { Verdict: var refused } => new(null, refused.Reason),
        };
    }

    /// <summary>
    /// A press answered before the confirmation, because the verdict does not allow it. The
    /// verdict's own reason, which is already under the pick: saying it again is what answers the
    /// press, since a button that appears to do nothing teaches nothing at all.
    /// </summary>
    public static CloseOutcome Refused(MemoryVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new(null, verdict.Reason);
    }

    /// <summary>
    /// Windows would not answer one of the calls a close is decided from. Nothing had been posted:
    /// everything that can fail this way happens before the first message.
    /// </summary>
    public static CloseOutcome Unanswered(Win32Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new(null, $"Windows would not answer: {failure.Message} Nothing was asked to close.");
    }
}
