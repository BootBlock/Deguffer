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
/// <para><b>Per copy, before it goes:</b> a copy the group keeps is held open, refusing every other
/// program's write, rename and delete, until the group's removals have finished; the copy to remove
/// is held open refusing write until it goes (<see cref="HeldCopy"/>); both are the files the search
/// found, with the same identity, length, last-modified time and attributes, neither online-only; and
/// they hold the same bytes and the same named streams apart from <c>Zone.Identifier</c>
/// (<see cref="CopyComparison"/>). The two are different files by construction: the copy kept is
/// chosen only among files whose identity no copy going shares, and each held handle is shown to be
/// its own file. A copy that fails any check stays, and the result says which.</para>
///
/// <para><b>Then the removal, files only.</b> Permanently through the handle that was compared, so
/// nothing put at the path since is what goes; or to the Recycle Bin, in batches
/// (<see cref="DuplicateRecycleBatch"/>), each copy described again as its batch goes and the bin's
/// item for it then identified, which must be the file compared. Where it is not, or that cannot be
/// told, the run stops there and names what went to the bin, where it can still
/// be restored. A copy the bin refuses is never deleted outright in its place.</para>
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
        using var batch = new DuplicateRecycleBatch(_bin, _files);

        void Go()
        {
            var went = batch.Go(outcomes, ct);
            stopped |= went.Stopped;
            cancelled |= went.Cancelled;
        }

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

            try
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
                            : RemoveOne(copy, kept, mode, batch, outcomes.Count, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled while the copy was compared, so nothing of it went.
                        cancelled = true;
                        outcome = NotReached(copy);
                    }

                    outcomes.Add(outcome);
                    stopped |= outcome.StopsTheRun;

                    if (batch.IsFull)
                    {
                        Go();
                    }
                }
            }
            finally
            {
                // The copy kept stays held until every copy of its group compared with it has gone.
                if (kept is not null && !batch.TakeOver(kept))
                {
                    kept.Dispose();
                }
            }
        }

        // The last batch, however the loop ended. After a cancel the bin reaches none of its copies, and
        // each is reported not reached.
        Go();

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

    /// <summary>
    /// Holds <paramref name="copy"/>, compares it with <paramref name="kept"/>, and deletes it, or
    /// adds it to <paramref name="batch"/> to go to the Recycle Bin with the copies around it. A copy
    /// added is answered as not reached until the batch goes and writes its outcome at
    /// <paramref name="slot"/>.
    /// </summary>
    private CopyRemoval RemoveOne(
        DuplicateCandidate copy, HeldCopy kept, ExploreRemovalMode mode, DuplicateRecycleBatch batch, int slot, CancellationToken ct)
    {
        var (held, check, why) = HeldCopy.Hold(copy, HeldFor.Removing, _files, _open);

        if (held is null)
        {
            return new CopyRemoval(copy, check, why);
        }

        ComparedCopy? compared = null;

        try
        {
            if (CopyComparison.Compare(kept.Content, kept.Copy.Length, held.Content, copy.Length, out var streams, ct) is { } differs)
            {
                return new CopyRemoval(copy, differs.Check, differs.Why);
            }

            compared = new ComparedCopy(held, kept, streams);
        }
        finally
        {
            if (compared is null)
            {
                held.Dispose();
            }
        }

        if (mode == ExploreRemovalMode.RecycleBin)
        {
            batch.Add(compared, slot);
            return NotReached(copy);
        }

        using (compared)
        {
            return compared.Changed(_files) ?? Delete(copy, held);
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
    /// Why this remover's Recycle Bin cannot take each of <paramref name="copies"/> as things are now,
    /// in the same order, with null for each where nothing shows it cannot: the question its removal
    /// refuses on, asked of the same bin with the same paths, so a confirmation lists for the bin only
    /// what the bin will take.
    /// </summary>
    internal IReadOnlyList<string?> WhyTheBinCannotTake(IReadOnlyList<DuplicateCandidate> copies) =>
        _bin.WhyItCannotTake([.. copies.Select(BinPath)]);

    /// <summary>
    /// The display form, normalised: the shell namespace refuses the extended-length prefix §6.3
    /// requires everywhere else. <see cref="IRecycleBin"/> says why that is a second seam.
    /// </summary>
    internal static string BinPath(DuplicateCandidate copy) => LongPath.Display(LongPath.Extended(copy.Path));

    internal static CopyRemoval NotReached(DuplicateCandidate copy) =>
        new(copy, RemovalCheck.NotReached, "The removal stopped before it reached this copy, so it is still where it was.");
}
