namespace Deguffer.Core.Execution;

/// <summary>
/// Which rows of the Storage preview are drawn, given the two filters the user can leave on.
///
/// <para>Both hide a row that offers no decision: one whose toolchain this machine does not have,
/// and one with nothing left to reclaim. Neither hides a row that has something to say, so a row is
/// drawn only when it passes both. A hidden row is still scanned, counted and kept in the list; it is
/// drawn or not, so switching a filter back on needs no rescan.</para>
///
/// <para>Each filter is read off <see cref="FindingStatus"/> rather than off the condition behind
/// it, because a second copy of that condition is free to disagree with the words on screen, and
/// what each filter promises is that it hides exactly the rows saying one thing.</para>
/// </summary>
/// <param name="ShowNotInstalled">
/// Whether to draw a row whose tool is genuinely not on this machine. Never a row waiting on a
/// folder the user can approve: that one is absent in the same way, and it is the row that says the
/// largest reclaimable thing on the disk is one setting away (§7).
/// </param>
/// <param name="ShowAlreadyClear">
/// Whether to draw a row saying "Already clear". The seven neighbouring states measure zero as well
/// and are not clear at all, and each is a thing the user may want to act on, so all seven stay
/// drawn.
/// </param>
public readonly record struct PreviewFilter(bool ShowNotInstalled, bool ShowAlreadyClear)
{
    /// <summary>
    /// Whether a row reporting <paramref name="status"/> is drawn.
    ///
    /// <para><b>A row this hides can carry no ticked step</b>, which is what makes hiding it safe
    /// behind a filter that is on by default. Both hidden states need
    /// <see cref="Finding.HasSomethingToRemove"/> to be false, which is no step answering
    /// <see cref="CleanupStep.RemovesSomething"/>, and <see cref="Choosing.StepChoice.CanBeSelected"/>
    /// refuses exactly those steps. The proof holds only while both sides ask that one property.</para>
    /// </summary>
    public bool Lists(FindingStatus status) => status switch
    {
        FindingStatus.ToolchainMissing => ShowNotInstalled,
        FindingStatus.AlreadyClear => ShowAlreadyClear,
        _ => true,
    };
}
