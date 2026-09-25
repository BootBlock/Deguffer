namespace Deguffer.Core.Execution;

/// <param name="Ran">Whether Windows' handler ran its cleanup to the end.</param>
/// <param name="Message">
/// What happened, written for the user, or null where nothing needs saying. Never null where it did
/// not run: a handler refuses for reasons this code cannot fix, and the number it refused with is the
/// only thing that tells one from another.
/// </param>
public sealed record DiskCleanupOutcome(bool Ran, string? Message = null);

/// <summary>What a handler says about a volume before anything is asked of it.</summary>
public enum DiskCleanupAnswer
{
    /// <summary>
    /// Windows registers no such handler that this process can load, or it would not start. Deleting
    /// the folders instead is not the fallback, because the handler is chosen for what it does besides
    /// deleting.
    /// </summary>
    Unavailable,

    /// <summary>
    /// This process cannot ask. Windows' setup handlers answer "nothing to delete" to every process
    /// that is not elevated, whatever is on the disk, so the answer here would be a guess. The step
    /// needs administrator rights to run in any case, and the plan made after elevating asks again.
    /// </summary>
    NeedsElevation,

    /// <summary>The handler finds nothing of its own to clear on this volume.</summary>
    NothingToClear,

    /// <summary>The handler has something to clear.</summary>
    HasSomething,
}

/// <param name="Answer">What the handler said.</param>
/// <param name="Message">Why, where it is a refusal, written for the user.</param>
public sealed record DiskCleanupSurvey(DiskCleanupAnswer Answer, string? Message = null)
{
    /// <summary>Whether a plan may offer the handler's directories on this answer.</summary>
    public bool MayOffer => Answer is DiskCleanupAnswer.HasSomething or DiskCleanupAnswer.NeedsElevation;

    /// <summary>
    /// The sentence for a plan that leaves <paramref name="path"/> standing on this answer, or null
    /// where the answer lets it be offered.
    /// </summary>
    public string? WhyLeftAlone(string handler, string path) => Answer switch
    {
        // Deleting the folder instead is not the fallback, because the handler is chosen for what it
        // does besides deleting.
        DiskCleanupAnswer.Unavailable =>
            $"Leaving {path} alone: Windows' own '{handler}' cleanup is not available to Deguffer here"
            + (Message is null ? "" : $" ({Message.TrimEnd('.')})")
            + ", and removing the folder by hand would leave behind what that cleanup also takes away.",

        // §5.1 is about whose knowledge decides, and here it is Windows'.
        DiskCleanupAnswer.NothingToClear =>
            $"Leaving {path} alone: Windows' own '{handler}' cleanup finds nothing of its own to clear "
            + "in it, so Deguffer does not clear it either.",

        _ => null,
    };
}

/// <summary>
/// Running one of the Disk Cleanup handlers Windows registers, behind an interface for the reason
/// <see cref="IRecycleBinEmptier"/> is one.
///
/// <para>The real handler deletes <c>Windows.old</c> on whatever machine runs it, so a test that
/// reached it would destroy the previous installation of whoever ran the suite. And §6.3 is a
/// requirement about the <em>form</em> of what crosses into Windows: the handler takes a volume in
/// display form, <c>C:\</c>, and no outcome of a cleanup can show which form crossed.</para>
/// </summary>
public interface IDiskCleanupHandlers
{
    /// <summary>
    /// Whether the handler has anything to clear on <paramref name="volume"/>, asked before a plan
    /// offers it and without clearing anything.
    ///
    /// <para><b>The handler's own answer, because §5.1 is about whose knowledge decides.</b> A folder
    /// can hold files the handler does not count as its own: the <em>Windows ESD installation
    /// files</em> handler was observed to answer "nothing to delete" over a <c>$Windows.~WS</c>
    /// holding 367 KB of setup sources. A plan that offered it would promise a reclaim Windows then
    /// declines, and the run would fail over a folder exactly as full as before.</para>
    /// </summary>
    DiskCleanupSurvey Survey(string handler, string volume, CancellationToken ct);

    /// <param name="handler">
    /// The handler's name, as registered under
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches</c>.
    /// </param>
    /// <param name="volume">
    /// The top of the volume to clear, in display form. The caller supplies it and this does not
    /// derive it, so the assertion about what crosses stays falsifiable.
    /// </param>
    /// <param name="ct">
    /// Handed to the handler through its progress callback, which is the only way to stop one:
    /// answering a report with <c>E_ABORT</c> asks it to stop at its next check.
    /// </param>
    DiskCleanupOutcome Run(string handler, string volume, CancellationToken ct);
}
