using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Which check a copy failed immediately before it would have gone, or that it passed them all and
/// went (§7.4). The result says which, so a copy left on the disk is never left without its reason.
/// </summary>
public enum RemovalCheck
{
    /// <summary>It passed every check and went.</summary>
    Removed,

    /// <summary>
    /// The confirmation listed it, and its mark no longer stands, judged as the removal began: it is
    /// refused now, or removing it would leave its group with no copy that can be kept.
    /// </summary>
    MarkNoLongerStands,

    /// <summary>No copy its group keeps could be held open and found unchanged since the search.</summary>
    NoKeptCopy,

    /// <summary>It is no longer there.</summary>
    Gone,

    /// <summary>Windows would not describe or open it, or another program holds it open; it may still be there.</summary>
    Unreadable,

    /// <summary>It went online-only since the search, so it is not on this device to compare.</summary>
    OnlyInTheCloud,

    /// <summary>
    /// It is not the file the search found as the search found it: another file or a link at its path,
    /// or a different length, last-modified time or attributes, or it changed while it was compared.
    /// </summary>
    Changed,

    /// <summary>Its bytes differ from the copy kept, whatever the checksum said.</summary>
    ContentDiffers,

    /// <summary>It holds a named stream the copy kept lacks or holds differently, apart from <c>Zone.Identifier</c>.</summary>
    StreamsDiffer,

    /// <summary>The Recycle Bin would not take it, and it was never deleted outright in its place.</summary>
    BinRefused,

    /// <summary>Windows would not delete it permanently.</summary>
    DeleteRefused,

    /// <summary>
    /// The Recycle Bin received a file that is not the one compared, which stops the run: the file it
    /// received is named so it can be restored.
    /// </summary>
    BinReceivedAnother,

    /// <summary>
    /// Windows moved it to the Recycle Bin and did not say where, or the item it named could not be
    /// identified, so nothing shows the bin received the file compared. The run stops.
    /// </summary>
    BinUnconfirmed,

    /// <summary>
    /// Windows deleted it outright when it was asked to move it to the Recycle Bin, so it is gone and
    /// not in the bin to restore. The run stops.
    /// </summary>
    DeletedOutright,

    /// <summary>The run stopped before it reached this copy, cancelled or at a copy the bin could not be shown to have taken.</summary>
    NotReached,
}

/// <summary>What became of one marked copy.</summary>
/// <param name="Message">What happened, in a sentence the page shows beside the copy.</param>
public sealed record CopyRemoval(DuplicateCandidate Copy, RemovalCheck Check, string Message)
{
    public bool Removed => Check == RemovalCheck.Removed;

    /// <summary>
    /// Whether the copy is gone from where it was: removed as the confirmation said, or deleted
    /// outright by Windows in place of the bin, which §5.6 then does not count as a loss beside it.
    /// </summary>
    public bool Went => Check is RemovalCheck.Removed or RemovalCheck.DeletedOutright;

    /// <summary>
    /// Whether this copy stops the run, because the bin cannot be shown to hold the file compared, or
    /// Windows deleted it outright instead.
    /// </summary>
    public bool StopsTheRun => Check is RemovalCheck.BinReceivedAnother or RemovalCheck.BinUnconfirmed or RemovalCheck.DeletedOutright;
}

/// <summary>What one duplicate removal did, and the §5.6 evidence that it did no more.</summary>
/// <param name="Copies">
/// Every copy the confirmation listed: first each whose mark no longer stood as the removal began,
/// then the rest in the order the removal reached them.
/// </param>
/// <param name="Cancelled">Whether the run was cancelled before it reached every copy.</param>
public sealed record DuplicateRemovalReport(
    ExploreRemovalMode Mode,
    IReadOnlyList<CopyRemoval> Copies,
    VerificationResult Verification,
    bool Cancelled)
{
    public IReadOnlyList<CopyRemoval> Removed => [.. Copies.Where(copy => copy.Removed)];

    public IReadOnlyList<CopyRemoval> Kept => [.. Copies.Where(copy => !copy.Went)];

    /// <summary>The copy at which the run stopped because the bin could not be shown to hold the file compared, if any.</summary>
    public CopyRemoval? StoppedAt => Copies.FirstOrDefault(copy => copy.StopsTheRun);

    /// <summary>
    /// What the removed copies occupied on disk, which a removal may free less than, and which the
    /// Recycle Bin frees only when it is emptied.
    /// </summary>
    public long SpaceRemoved => Removed.Sum(copy => copy.Copy.SizeOnDisk);

    /// <summary>
    /// What happened, in one sentence for the status line, with the run's stop and the §5.6 result
    /// attached whenever they say something went other than planned.
    /// </summary>
    public string Summary
    {
        get
        {
            // A copy Windows deleted outright went, and the stop names it, so "nothing" is said of the bin alone.
            var did = Removed.Count == 0
                ? Copies.Any(copy => copy.Check is RemovalCheck.DeletedOutright) ? "Nothing went to the Recycle Bin." : "Nothing was removed."
                : Mode == ExploreRemovalMode.RecycleBin
                    ? $"Moved {Count(Removed.Count)} ({FreeSpace.Format(SpaceRemoved)}) to the Recycle Bin. The drive gets the space once the bin is emptied."
                    : $"Deleted {Count(Removed.Count)} ({FreeSpace.Format(SpaceRemoved)}).";

            var sentence = Kept.Count == 0 ? did : $"{did} {Count(Kept.Count)} stayed, each with its reason.";

            if (StoppedAt is { } stopped)
            {
                sentence = $"Stopped at '{Path.GetFileName(stopped.Copy.Path)}'. {stopped.Message} {sentence}";
            }
            else if (Cancelled)
            {
                sentence = $"Stopped part-way. {sentence}";
            }

            return Verification.Passed
                ? sentence
                : $"{sentence} {Verification.Unpassed.Count} of {Verification.Checks.Count} "
                  + "check(s) on what should have survived did not pass. Look at the folders before doing anything else.";
        }
    }

    private static string Count(int count) => count == 1 ? "1 copy" : $"{count:N0} copies";
}
