using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="Removed">Whether the item is gone from where it was.</param>
/// <param name="Message">
/// What happened, written for the user. Never null on a failure: a Recycle Bin removal has more
/// ways to be refused than an outright delete — the shell will not take a path it cannot parse, and
/// it will not recycle onto a volume with no bin — and "nothing happened" with no sentence beside it
/// is the answer a user cannot act on.
/// </param>
public sealed record RecycleOutcome(bool Removed, string? Message = null)
{
    /// <summary>
    /// The outcome of an item the removal was cancelled before reaching. It is where it was, and a
    /// caller reports it as not reached rather than as refused.
    /// </summary>
    public static RecycleOutcome NotReached { get; } = new(
        Removed: false,
        "The removal was cancelled before Windows moved this to the Recycle Bin, so it is still where it was.")
    {
        Cancelled = true,
    };

    /// <summary>
    /// The outcome of an item the batch stopped before reaching, because the caller's
    /// <c>stopsAfter</c> rule stopped it at an earlier item (<see cref="IRecycleBin.Recycle"/>). It is
    /// where it was.
    /// </summary>
    public static RecycleOutcome StoppedBefore { get; } = new(
        Removed: false,
        "Deguffer stopped before Windows moved this to the Recycle Bin, so it is still where it was.")
    {
        Stopped = true,
    };

    /// <summary>
    /// Where the bin put the item, as the shell said once it had moved it, or null where the shell
    /// did not say. A duplicate removal identifies what is there, to check that the file the bin
    /// received is the file it compared (§7.4).
    /// </summary>
    public string? Binned { get; init; }

    /// <summary>
    /// Whether Windows deleted the item outright when it was asked to move it to the bin. It is gone,
    /// and it is not in the bin to restore. The shell does this, reporting success, for a path it
    /// cannot take (<see cref="RecycleBinReach"/>), so it is refused beforehand; this is what the
    /// shell's progress sink says when a path changed under that question or another reason arose.
    /// </summary>
    public bool DeletedOutright { get; init; }

    /// <summary>
    /// Whether the removal was cancelled before the item was moved (<see cref="NotReached"/>). Nothing
    /// happened to it.
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// Whether the batch stopped before the item, at an earlier item the caller's rule stopped it at
    /// (<see cref="StoppedBefore"/>). Nothing happened to it.
    /// </summary>
    public bool Stopped { get; init; }

    /// <summary>
    /// What the user is told about an item the bin cannot take, for <paramref name="why"/>
    /// (<see cref="IRecycleBin.WhyItCannotTake"/>). Nothing was handed to the shell, so it says so.
    /// </summary>
    internal static string CannotTake(string why) =>
        $"{why} Deguffer did not ask Windows to move it, and it is still where it was.";
}

/// <summary>
/// Moving items to the Recycle Bin, behind an interface for the reason
/// <see cref="IFileSystem"/> is: §6.3 is a requirement about the <em>form</em> of the path that
/// crosses into Win32, and no outcome of a deletion can demonstrate it.
///
/// <para><b>The form required here is not the one <see cref="IFileSystem"/> requires</b>, and that
/// is the whole reason this is a second seam rather than a method on the first. Everything else in
/// Core hands Win32 the extended-length prefix, because the file APIs need it past
/// <c>MAX_PATH</c>. The shell namespace does not accept it at all: <c>SHCreateItemFromParsingName</c>
/// parses <c>C:\Users\…</c> and refuses <c>\\?\C:\Users\…</c>. So what crosses this boundary is the
/// fully-qualified, fully-resolved <em>display</em> path — normalised, because
/// <see cref="LongPath.Configured"/>'s trap applies to a shell call exactly as it applies to a
/// deletion, and a value carrying <c>..</c> would recycle a directory nobody named. The shell also
/// rereads a name ending in a dot, and a last name ending in a space, as another, and no spelling it
/// takes keeps them, so such an item is refused (<see cref="RecycleBinReach"/>).</para>
///
/// <para>A path too long for the shell is therefore a refusal rather than a truncation, and it is
/// reported as one. Falling back to an outright delete would be worse than failing: the user asked
/// for the reversible removal, and quietly giving them the irreversible one is the single change
/// §7.1 would least tolerate. That is also why an item the bin cannot take is refused before the
/// shell is asked: the shell itself deletes such an item outright and reports success
/// (<see cref="RecycleBinReach"/>).</para>
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// The most items one <see cref="Recycle"/> call takes. Each costs the shell an operation of its
    /// own, so a removal of thousands hands them over in batches: measured on 2026-11-05, 300 small
    /// files took 8.3 seconds one operation each and 2.2 seconds in one. The bound is for what a batch
    /// widens. Every item is asked whether the bin can take it before the batch goes, so the last item
    /// moved was asked that the batch's length earlier; and a duplicate removal holds every copy of a
    /// batch open, with the copy each of their groups keeps, until the batch has gone.
    /// </summary>
    const int LargestBatch = 128;

    /// <summary>
    /// Moves each of <paramref name="paths"/> to the Recycle Bin, in one operation, answering what
    /// became of each, in the same order. Each item is asked again whether the bin can take it
    /// (<see cref="WhyItCannotTake"/>) and is refused without reaching the shell where it cannot. A
    /// refusal of one item does not stop the others.
    /// </summary>
    /// <param name="paths">
    /// At most <see cref="LargestBatch"/>, each fully qualified, fully resolved, and without the
    /// extended-length prefix. The caller normalises; this does not, because a seam that silently
    /// corrected its input would make the §6.3 assertion above unfalsifiable.
    /// </param>
    /// <param name="ct">
    /// Stops the batch before its next item, which, with every item after it, is answered
    /// <see cref="RecycleOutcome.NotReached"/>. Never thrown: what was moved before it is answered as
    /// moved, so a caller can report it and verify it.
    /// </param>
    /// <param name="stopsAfter">
    /// Asked, with its position, about each item the shell has dealt with, as soon as it has, and
    /// before the shell reaches the next. Where it answers true the batch stops there: every item
    /// after it is answered <see cref="RecycleOutcome.StoppedBefore"/>. A duplicate removal stops at
    /// a copy Windows deleted outright or the bin cannot be shown to hold (§7.4), as it did when each
    /// copy was an operation of its own. Called on the bin's own thread, so it must not throw.
    /// </param>
    IReadOnlyList<RecycleOutcome> Recycle(
        IReadOnlyList<string> paths, CancellationToken ct = default, Func<int, RecycleOutcome, bool>? stopsAfter = null);

    /// <summary>
    /// Why the bin cannot take the item at <paramref name="path"/> as things are now, or null where
    /// nothing shows it cannot. The answer <see cref="Recycle"/> refuses on, asked before a
    /// confirmation so it promises the bin only for what the bin will take; <see cref="Recycle"/>
    /// asks again, because the item or the bin can change while the user reads.
    /// </summary>
    /// <param name="path">In the form <see cref="Recycle"/> takes.</param>
    /// <param name="ct">Stops the walk a folder needs, which is of everything inside it.</param>
    string? WhyItCannotTake(string path, CancellationToken ct = default);
}
