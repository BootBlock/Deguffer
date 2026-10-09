using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Duplicates;

/// <summary>Marks the file a handle is open on for deletion, answering the Win32 error, or zero where it is marked.</summary>
internal delegate int HandleDeleter(SafeFileHandle handle);

/// <summary>One group a removal works through: its copies whose marks stand, in the order they go.</summary>
internal sealed record PlannedGroup(DuplicateGroup Group, IReadOnlyList<DuplicateCandidate> Going);

/// <summary>
/// Removes the copies whose marks stand (§7.4), each only once it is shown, immediately before it
/// goes, to hold what a copy its group keeps holds, then asserts that what should have survived did.
///
/// <para><b>Decided again as it begins.</b> The marks are judged against the machine as it is when
/// the removal starts (<see cref="DuplicateMarks.RejudgeAsync"/>): Explore's policy, Storage's places,
/// the program folders, the temporary folder, the cloud folders and each drive's disks, read then.
/// What goes is the marks that stand under that judgement (<see cref="GroupMarks.Standing"/>), which
/// refuses every refused copy and every mark that would leave a group with no copy that can be
/// kept.</para>
///
/// <para><b>Per copy, immediately before it goes:</b> a copy the group keeps is held open, refusing
/// every other program's write, rename and delete, until the group's removals have finished; the copy
/// to remove is held open refusing write (<see cref="HeldCopy"/>); both are the files the search
/// found, with the same identity, length, last-modified time and attributes, neither online-only; and
/// they hold the same bytes and the same named streams apart from <c>Zone.Identifier</c>
/// (<see cref="CopyComparison"/>). The two are different files by construction: the copy kept is
/// chosen only among files whose identity no copy going shares, and each held handle is shown to be
/// its own file. A copy that fails any check stays, and the result says which.</para>
///
/// <para><b>Then the removal, files only.</b> Permanently through the handle that was compared, so
/// nothing put at the path since is what goes; or to the Recycle Bin, whose item is then identified
/// and must be the file compared. Where it is not, or that cannot be told, the run stops there and
/// names what went to the bin, where it can still be restored. A copy the bin refuses is never
/// deleted outright in its place.</para>
///
/// <para><b>§5.6 afterwards</b>: every copy left in a group is still there by its file ID
/// (<see cref="CopySurvival"/>), and everything beside each removed copy by its exact name
/// (<see cref="SiblingCheck"/>), as Explore asserts.</para>
/// </summary>
public sealed class DuplicateRemover
{
    private readonly FileInformation _files;
    private readonly CopyOpener _open;
    private readonly HandleDeleter _delete;
    private readonly IRecycleBin _bin;
    private readonly IFileSystem _fs;

    public static DuplicateRemover Default { get; } = new(
        FileInformation.Default,
        FileInformation.OpenHeld,
        HandleDeletion.Delete,
        ShellRecycleBin.Default,
        WindowsFileSystem.Default);

    /// <param name="files">Describes each copy by its path, the bin's item, and every copy left by its number.</param>
    /// <param name="open">
    /// Opens each copy held by its path, so a test can see the path reach Windows in its extended form
    /// (§6.3).
    /// </param>
    /// <param name="delete">
    /// Deletes through the compared handle, so a test can put another file at the copy's path, or try
    /// to delete the copy kept, at the last moment before the removal.
    /// </param>
    internal DuplicateRemover(
        FileInformation files,
        CopyOpener open,
        HandleDeleter delete,
        IRecycleBin bin,
        IFileSystem fs)
    {
        _files = files;
        _open = open;
        _delete = delete;
        _bin = bin;
        _fs = fs;
    }

    /// <summary>
    /// Remove the copies <paramref name="confirmed"/> listed whose marks still stand in
    /// <paramref name="marks"/>, judged against the machine as it is now, the way it says.
    ///
    /// <para><b>Never more than the confirmation listed.</b> A mark the confirmation dropped can stand
    /// again under a later judgement, and the copy would then go without the user having seen it
    /// listed.</para>
    /// </summary>
    /// <param name="protections">Built afresh for this removal, so what the machine protects is read now.</param>
    public async Task<DuplicateRemovalReport> RemoveAsync(
        DuplicateMarks marks,
        RemovalConfirmation confirmed,
        MachineProtections protections,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(confirmed);

        var keeping = await marks.RejudgeAsync(protections, ct).ConfigureAwait(false);
        var (plan, dropped) = Plan(marks.Groups, confirmed.Copies, keeping);

        // From here the disk can change under the groups, however the run ends.
        marks.RemovalBegan();

        // Off the calling thread, which is a page resuming on the UI thread after its dialog: every
        // copy is read whole, twice. Not cancelled by the token here, because a run that has begun
        // reports what it did and verifies it, however it ends.
        return await Task.Run(() => Remove(plan, dropped, keeping, confirmed.Mode, ct), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Every group with a listed copy whose mark stands under <paramref name="keeping"/>, with those
    /// copies, and each listed copy whose mark no longer stands, with why.
    /// </summary>
    /// <param name="listed">The copies the confirmation listed.</param>
    internal static (IReadOnlyList<PlannedGroup> Plan, IReadOnlyList<CopyRemoval> Dropped) Plan(
        IReadOnlyList<GroupMarks> groups, IReadOnlyList<DuplicateCandidate> listed, CopyKeeping keeping)
    {
        HashSet<FileIdentity> confirmed = [.. listed.Select(copy => copy.Identity)];
        List<PlannedGroup> plan = [];
        List<CopyRemoval> dropped = [];

        foreach (var group in groups)
        {
            var standing = group.Standing(keeping);
            List<DuplicateCandidate> going = [.. standing.Where(copy => confirmed.Contains(copy.Identity))];

            dropped.AddRange(group.Group.Files
                .Where(copy => confirmed.Contains(copy.Identity) && !standing.Any(marked => marked.Identity == copy.Identity))
                .Select(copy => new CopyRemoval(
                    copy,
                    RemovalCheck.MarkNoLongerStands,
                    keeping.WhyNotMarked(copy)
                    ?? (group.IsMarked(copy)
                        ? "Removing it now would leave its group with no copy that can be kept, so it was not removed."
                        : "It is no longer marked, so it was not removed."))));

            if (going.Count > 0)
            {
                plan.Add(new PlannedGroup(group.Group, going));
            }
        }

        return (plan, dropped);
    }

    /// <param name="dropped">Each listed copy whose mark no longer stands, reported first.</param>
    /// <param name="keeping">The keeping rule as it was judged for this removal, which chooses the copy each group keeps.</param>
    internal DuplicateRemovalReport Remove(
        IReadOnlyList<PlannedGroup> plan,
        IReadOnlyList<CopyRemoval> dropped,
        CopyKeeping keeping,
        ExploreRemovalMode mode,
        CancellationToken ct)
    {
        // Both taken before anything goes: evidence gathered afterwards can only describe what is left.
        var siblings = SiblingCheck.Take(plan.SelectMany(planned => planned.Going).Select(copy => copy.Path), _fs);
        var survivors = CopySurvival.Take(
            plan.SelectMany(planned => planned.Group.Files.Where(copy => !Goes(planned, copy))),
            _files);

        List<CopyRemoval> outcomes = [.. dropped];
        var cancelled = false;
        var stopped = false;

        foreach (var planned in plan)
        {
            var going = planned.Going;

            if (cancelled || stopped || ct.IsCancellationRequested)
            {
                cancelled |= !stopped;
                outcomes.AddRange(going.Select(NotReached));
                continue;
            }

            var (kept, why) = HoldKept(planned, keeping);

            using (kept)
            {
                foreach (var copy in going)
                {
                    if (cancelled || stopped || ct.IsCancellationRequested)
                    {
                        cancelled |= !stopped;
                        outcomes.Add(NotReached(copy));
                        continue;
                    }

                    CopyRemoval outcome;

                    try
                    {
                        outcome = kept is null
                            ? new CopyRemoval(copy, RemovalCheck.NoKeptCopy, why!)
                            : RemoveOne(copy, kept, mode, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled while the copy was compared, so nothing of it went.
                        cancelled = true;
                        outcome = NotReached(copy);
                    }

                    outcomes.Add(outcome);
                    stopped |= outcome.StopsTheRun;
                }
            }
        }

        HashSet<FileIdentity> removed = [.. outcomes.Where(outcome => outcome.Went).Select(outcome => outcome.Copy.Identity)];
        List<VerificationCheck> checks =
        [
            .. siblings.Verify(outcomes.Where(outcome => outcome.Went).Select(outcome => outcome.Copy.Path)),
            .. survivors.Verify(removed),
        ];

        return new DuplicateRemovalReport(mode, outcomes, new VerificationResult { Checks = checks }, cancelled);
    }

    /// <summary>Whether <paramref name="copy"/> is a file going in <paramref name="planned"/>, by its identity, never its path.</summary>
    private static bool Goes(PlannedGroup planned, DuplicateCandidate copy) =>
        planned.Going.Any(marked => marked.Identity == copy.Identity);

    /// <summary>
    /// A copy <paramref name="planned"/>'s group keeps, held open refusing every other program's
    /// write, rename and delete, once it is shown to be the file the search found, unchanged; or why
    /// none could be. Never a file going, known by its identity, so a stale group naming one file by
    /// two paths cannot have the file kept and removed at once.
    /// </summary>
    private (HeldCopy? Kept, string? Why) HoldKept(PlannedGroup planned, CopyKeeping keeping)
    {
        string? first = null;

        foreach (var copy in planned.Group.Files.Where(copy => !Goes(planned, copy) && keeping.WhyNotKept(copy) is null))
        {
            var (kept, _, why) = HeldCopy.Hold(copy, HeldFor.Keeping, _files, _open);

            if (kept is not null)
            {
                return (kept, null);
            }

            first ??= $"'{copy.Path}': {why}";
        }

        return (null, first is null
            ? "No copy in this group can be kept now, so nothing in it was removed."
            : $"No copy this group keeps could be held open and found unchanged since the search, so nothing in it was removed. {first}");
    }

    private CopyRemoval RemoveOne(DuplicateCandidate copy, HeldCopy kept, ExploreRemovalMode mode, CancellationToken ct)
    {
        var (held, check, why) = HeldCopy.Hold(copy, HeldFor.Removing, _files, _open);

        if (held is null)
        {
            return new CopyRemoval(copy, check, why);
        }

        using (held)
        {
            if (CopyComparison.Compare(kept.Content, kept.Copy.Length, held.Content, copy.Length, out var streams, ct) is { } differs)
            {
                return new CopyRemoval(copy, differs.Check, differs.Why);
            }

            // Sharing refuses a write, not a change to times or attributes, nor a stream added
            // beside the content, so both are described again once they are compared.
            if ((kept.Recheck(_files) ?? held.Recheck(_files)) is not null
                || CopyComparison.StreamsOf(held.Content) is not { } after
                || !CopyComparison.SameStreams(streams, after))
            {
                return new CopyRemoval(copy, RemovalCheck.Changed,
                    "It or the copy kept changed while they were compared, so the comparison no longer shows they are the same.");
            }

            return mode == ExploreRemovalMode.Permanent ? Delete(copy, held) : Recycle(copy, held);
        }
    }

    /// <summary>
    /// Deletes the copy through the handle that was compared, which takes effect as it is closed, so
    /// whatever is at the copy's path by then is not what goes.
    /// </summary>
    private CopyRemoval Delete(DuplicateCandidate copy, HeldCopy held)
    {
        var error = _delete(held.Content);

        return error == 0
            ? new CopyRemoval(copy, RemovalCheck.Removed, "Deleted permanently.")
            : new CopyRemoval(copy, RemovalCheck.DeleteRefused, $"Windows would not delete it (error {error}), so it is still where it was.");
    }

    /// <summary>
    /// Moves the copy to the Recycle Bin, then identifies what the bin received: it must be the file
    /// that was compared. The copy is still held, sharing deleting, so the shell can move it.
    ///
    /// <para>Identified through the handle the copy was compared through, which follows the file it is
    /// open on wherever it is moved: measured on 2026-10-09 on NTFS, FAT32 and exFAT, and on an NTFS
    /// volume mounted in a folder, the handle's final path was the bin item the shell named. A file's number cannot do it everywhere, because
    /// FAT32 and exFAT number a file by where its entry lies, and a move into the bin changes it. Where
    /// the volume keeps a file's number, the item is identified by it as well.</para>
    /// </summary>
    private CopyRemoval Recycle(DuplicateCandidate copy, HeldCopy held)
    {
        // The display form, normalised: the shell namespace refuses the extended-length prefix
        // §6.3 requires everywhere else. IRecycleBin says why that is a second seam.
        var recycled = _bin.Recycle(LongPath.Display(LongPath.Extended(copy.Path)));

        if (!recycled.Removed)
        {
            return new CopyRemoval(copy, RemovalCheck.BinRefused,
                $"{recycled.Message ?? "Windows would not move it to the Recycle Bin."} Deguffer never deletes a copy outright in its place.");
        }

        if (recycled.DeletedOutright)
        {
            return new CopyRemoval(copy, RemovalCheck.DeletedOutright,
                $"Windows deleted '{copy.Path}' outright rather than moving it to the Recycle Bin, so it cannot be restored "
                + "from there, and Deguffer stopped.");
        }

        if (recycled.Binned is not { } binned)
        {
            return new CopyRemoval(copy, RemovalCheck.BinUnconfirmed,
                $"Windows moved '{copy.Path}' to the Recycle Bin and did not say where, so Deguffer cannot show the bin received "
                + "the file it compared, and stopped. Restore it from the Recycle Bin if it is not the copy you meant to remove.");
        }

        var receivedAnother = new CopyRemoval(copy, RemovalCheck.BinReceivedAnother,
            $"The Recycle Bin received a file from '{copy.Path}' that is not the copy Deguffer compared, so it stopped. "
            + "That file is in the Recycle Bin, where it can be restored, and the copy compared is not.");

        if (FileInformation.FinalPathOf(held.Content) is not { } heldAt)
        {
            return new CopyRemoval(copy, RemovalCheck.BinUnconfirmed,
                $"Windows moved '{copy.Path}' to the Recycle Bin, and would not say where the file Deguffer compared is now, so "
                + "Deguffer cannot show the bin received it, and stopped. Restore it from the Recycle Bin if it is not the copy you meant to remove.");
        }

        if (!string.Equals(LongPath.Display(heldAt), binned, StringComparison.Ordinal))
        {
            return receivedAnother;
        }

        if (!copy.Volume.KeepsFileNumbers)
        {
            return new CopyRemoval(copy, RemovalCheck.Removed, "Moved to the Recycle Bin.");
        }

        return _files.Describe(binned, copy.Route) switch
        {
            { Description: { } item } when item.Identity == copy.Identity =>
                new CopyRemoval(copy, RemovalCheck.Removed, "Moved to the Recycle Bin."),
            { Description: not null } => receivedAnother,
            _ => new CopyRemoval(copy, RemovalCheck.BinUnconfirmed,
                $"Windows moved '{copy.Path}' to the Recycle Bin, and would not describe the item it put there, so Deguffer "
                + "cannot show it is the file it compared, and stopped. Restore it from the Recycle Bin if it is not the copy you meant to remove."),
        };
    }

    private static CopyRemoval NotReached(DuplicateCandidate copy) =>
        new(copy, RemovalCheck.NotReached, "The removal stopped before it reached this copy, so it is still where it was.");
}
