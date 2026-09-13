using Deguffer.Core.Execution;

namespace Deguffer.Core.Memory.Acting;

/// <summary>Where one close has got to (§7.2.1).</summary>
public enum CloseState
{
    /// <summary>
    /// The messages are posted and Deguffer is watching the handle it holds. There is no deadline: a
    /// save prompt waits for a person, so a timer would report "still running" about a program doing
    /// exactly what it was asked.
    /// </summary>
    Watching,

    /// <summary>The process exited.</summary>
    Closed,

    /// <summary>
    /// The watch ended with the process still running, because the user dismissed the result or left
    /// the page. It asked, it refused, or it has work Deguffer cannot see, and Deguffer offers
    /// nothing stronger, because there is nothing stronger to offer.
    /// </summary>
    StillRunning,
}

/// <summary>
/// What one close did, in the words the user reads, with the §5.6 evidence behind it (§7.2.1).
///
/// <para>The words are here rather than in the shell for the reason
/// <see cref="Execution.RunOutcome"/>'s are: §5.6 is reported rather than only performed, and a
/// sentence that exists only inside a page is one nothing can hold Deguffer to. The page redraws
/// every couple of seconds and clears its own notes each time, so this report is a surface of its
/// own that the user dismisses.</para>
///
/// <para><b>The after figures exist only where the process exited</b>, which the three factories
/// below make the only way to build one. A result still watching, or one whose program is still
/// running, shows no after figure at all rather than a difference that means nothing yet.</para>
/// </summary>
public sealed record CloseReport
{
    private CloseReport(
        ProcessMemory target,
        int windows,
        CloseState state,
        SystemMemory before,
        SystemMemory? after,
        VerificationResult verification,
        int moved)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);
        ArgumentOutOfRangeException.ThrowIfNegative(moved);

        (Target, Windows, State, Before, After, Verification, Moved) =
            (target, windows, state, before, after, verification, moved);
    }

    /// <summary>The process that was asked to close, as the snapshot before the action had it.</summary>
    public ProcessMemory Target { get; }

    /// <summary>How many of its windows were asked.</summary>
    public int Windows { get; }

    /// <summary>
    /// How many of the windows the confirmation counted were passed over, because the process that
    /// owned each of them had changed between the survey and the moment of posting (§7.2.1). Those
    /// received nothing, and the difference is said rather than smoothed over: the user was told a
    /// number in the dialog.
    /// </summary>
    public int Moved { get; }

    public CloseState State { get; }

    /// <summary>The machine as the first message was posted.</summary>
    public SystemMemory Before { get; }

    /// <summary>The machine as the process exited, or null where it has not.</summary>
    public SystemMemory? After { get; }

    /// <summary>
    /// §5.6, once the watch has ended. Empty while the watch is open, because nothing has been
    /// looked for yet, and an empty result passes rather than claiming anything.
    /// </summary>
    public VerificationResult Verification { get; }

    /// <summary>
    /// What happened, for the reader. It states what the program did and what Deguffer will not do
    /// next, and it never offers anything stronger, because §7.2.1 has nothing stronger to offer.
    ///
    /// <para>A failed §5.6 assertion is carried here as well, for the reason
    /// <see cref="Exploring.Acting.ExploreRemovalReport.Summary"/> carries one: whether the user is
    /// told that a rule reached further than it was meant to must not depend on which surface is
    /// rendering the report.</para>
    /// </summary>
    public string Statement
    {
        get
        {
            var sentence = State switch
            {
                CloseState.Watching =>
                    $"{Target.Named} was asked to close. {Asked()} Deguffer is waiting for it to exit "
                    + "and will send nothing else.",

                CloseState.Closed => $"{Target.Named} closed.",

                _ =>
                    $"{Target.Named} is still running. It may have asked you about unsaved work, it "
                    + "may have refused, or it may have work Deguffer cannot see. Deguffer sent it "
                    + "nothing else, and you can pick it again.",
            };

            if (Moved > 0)
            {
                sentence += Moved == 1
                    ? " One window Deguffer counted had passed to another program by then, and "
                      + "received nothing."
                    : $" {Moved} windows Deguffer counted had passed to another program by then, and "
                      + "received nothing.";
            }

            return Verification.Passed
                ? sentence
                : $"{sentence} {Verification.Failures.Count} check(s) on what this close could not "
                  + "have ended did not pass. Look at the report before doing anything else.";
        }
    }

    /// <summary>
    /// Commit charge and available memory, in the words §7.2's headline uses, taken twice where the
    /// process exited.
    ///
    /// <para>The sentence about the rest of the machine is not a hedge. Windows goes on allocating
    /// and freeing throughout a watch that may last as long as somebody takes to answer a save
    /// prompt, so the difference between the two reads is what happened while Deguffer watched
    /// rather than what this close returned.</para>
    /// </summary>
    public string Figures =>
        After is { } after
            ? $"{MemoryHeadline.Commit(Before)} when the close was sent, and "
              + $"{MemoryHeadline.Available(Before)}. {MemoryHeadline.Commit(after)} when it exited, "
              + $"and {MemoryHeadline.Available(after)}. The machine went on allocating and freeing "
              + "throughout, so the difference is what happened while Deguffer watched rather than "
              + "what this close returned."
            : $"{MemoryHeadline.Commit(Before)} when the close was sent, and "
              + $"{MemoryHeadline.Available(Before)}.";

    /// <summary>The close as it stands the moment the last message is posted.</summary>
    public static CloseReport Watching(ProcessMemory target, int windows, SystemMemory before, int moved = 0) =>
        new(target, windows, CloseState.Watching, before, after: null, new VerificationResult(), moved);

    /// <summary>The close once the process has exited, which is the only way an after figure exists.</summary>
    public static CloseReport Closed(
        ProcessMemory target,
        int windows,
        SystemMemory before,
        SystemMemory after,
        VerificationResult verification,
        int moved = 0)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(verification);

        return new CloseReport(target, windows, CloseState.Closed, before, after, verification, moved);
    }

    /// <summary>The close once the watch has ended with the program still running.</summary>
    public static CloseReport StillRunning(
        ProcessMemory target,
        int windows,
        SystemMemory before,
        VerificationResult verification,
        int moved = 0)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return new CloseReport(
            target, windows, CloseState.StillRunning, before, after: null, verification, moved);
    }

    /// <summary>
    /// How many windows were asked, in words rather than as an "(s)": the count is the whole point of
    /// the sentence, and a program with three documents open asks about each in its own words.
    /// </summary>
    private string Asked() => Windows == 1 ? "Its window was asked." : $"Each of its {Windows} windows was asked.";
}
