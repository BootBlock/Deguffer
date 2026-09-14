namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What the user is asked before Memory asks a program to close, and what they are told it costs
/// (§7.2.1).
///
/// <para>A Core type for the reason <see cref="Exploring.Acting.ExploreRemovalPrompt"/> is one, and
/// more so. Tier 3's typed phrase is a preference because the preview and Tier 3 never being
/// pre-selected still stand behind it. Nothing stands behind this one: a posted message cannot be
/// recalled, and what is at risk is another program's unsaved state. So a close is confirmed every
/// time, and the sentence that confirms it is held to a test rather than living inside a
/// dialog.</para>
///
/// <para>It classifies nothing and recommends nothing (§7.2). It says which program, how many
/// windows will be asked, that the program may ask about unsaved work or refuse outright, and that
/// Deguffer does nothing further either way — because there is nothing further it will do.</para>
/// </summary>
/// <param name="Title">The question, naming the program and its identifier.</param>
/// <param name="Consequence">What Deguffer sends, what the program may do about it, and what Deguffer will not do next.</param>
/// <param name="ConfirmLabel">The affirmative button.</param>
public sealed record MemoryClosePrompt(string Title, string Consequence, string ConfirmLabel)
{
    /// <param name="target">The process the user picked, as the snapshot they picked it from describes it.</param>
    /// <param name="windows">
    /// How many of its windows qualify (§7.2.1). Named in the dialog because closing one window of a
    /// program that has several does not close the program, and three save prompts are not a
    /// surprise the user should meet afterwards.
    /// </param>
    /// <param name="listing">
    /// How much of the service list the snapshot obtained. The close refuses a process hosting a
    /// service, and that rule reaches only the hosts Windows named, so the dialog carries what the
    /// picture says about the rest (§7.2.1).
    /// </param>
    public static MemoryClosePrompt For(ProcessMemory target, int windows, ServiceListing listing)
    {
        ArgumentNullException.ThrowIfNull(target);

        // A process with no window that qualifies is refused rather than attempted (§7.2.1), so a
        // confirmation about none of them is a dialog that should never have been built.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);

        // Both grammatical forms written out rather than an "(s)", because this sentence is the one
        // the user reads before a program is asked to give up unsaved work.
        var asking = windows == 1
            ? "Deguffer asks its window to close, the way that window's own close button does."
            : $"Deguffer asks each of its {windows} windows to close, the way each window's own close "
              + "button does. A program with several documents open asks about each in its own words.";

        return new MemoryClosePrompt(
            $"Ask {target.Named} to close?",

            $"{asking} {target.Name} may ask you about unsaved work, and it may refuse to close at "
            + "all. Deguffer sends nothing else, waits, and does nothing further whatever it decides. "
            + "Deguffer never closes a service host, and cannot be certain this is not one: "
            + MemoryNotes.WhichHostsAreMissing(listing),

            "Ask it to close");
    }
}
