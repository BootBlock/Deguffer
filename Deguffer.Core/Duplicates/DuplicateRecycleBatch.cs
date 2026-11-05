using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The copies of one duplicate removal waiting to go to the Recycle Bin in one shell operation
/// (<see cref="IRecycleBin.LargestBatch"/>), each held and compared, with the copy each of their
/// groups keeps held until the batch has gone.
///
/// <para><b>Described again as the batch goes.</b> Each copy and the copy its group keeps are
/// described again immediately before the batch is handed to the bin
/// (<see cref="ComparedCopy.Changed"/>), so the wait for the rest of the batch is inside what is
/// checked. A copy that changed stays, and the others go.</para>
///
/// <para><b>Then identified one by one.</b> The bin moves the batch and says, for each copy, where
/// it put it. Each copy is identified there through the handle it was compared through, which follows
/// the file it is open on wherever it is moved: measured on 2026-10-09 on NTFS, FAT32 and exFAT, and
/// on an NTFS volume mounted in a folder, the handle's final path was the bin item the shell named. A
/// file's number cannot do it everywhere, because FAT32 and exFAT number a file by where its entry
/// lies, and a move into the bin changes it. Where the volume keeps a file's number, the item is
/// identified by it as well.</para>
///
/// <para><b>A copy that stops the run stops it there.</b> Each copy is identified as soon as the shell
/// has dealt with it, before the shell reaches the next, and a copy Windows deleted outright or the
/// bin cannot be shown to hold stops the batch at that copy (§7.4): the copies after it in the batch
/// are not reached, as they were not when each copy was an operation of its own.</para>
/// </summary>
internal sealed class DuplicateRecycleBatch(IRecycleBin bin, FileInformation files) : IDisposable
{
    private readonly List<(ComparedCopy Copy, int Slot)> _waiting = [];
    private readonly List<HeldCopy> _kept = [];

    public bool IsFull => _waiting.Count == IRecycleBin.LargestBatch;

    /// <summary>
    /// Adds <paramref name="compared"/>, taking over its handle, to go with the batch. Its outcome is
    /// written at <paramref name="slot"/> in the run's outcomes when the batch goes.
    /// </summary>
    public void Add(ComparedCopy compared, int slot) => _waiting.Add((compared, slot));

    /// <summary>
    /// Takes over the handle of a group's kept copy once the group's copies have all been reached, to
    /// close it when its copies here have gone; or answers false where none of them waits here, for
    /// the caller to close it now.
    /// </summary>
    public bool TakeOver(HeldCopy kept)
    {
        if (!_waiting.Any(waiting => ReferenceEquals(waiting.Copy.Kept, kept)))
        {
            return false;
        }

        _kept.Add(kept);
        return true;
    }

    /// <summary>
    /// Moves the waiting copies to the bin, writes each one's outcome into <paramref name="outcomes"/>,
    /// and closes every handle the batch held. Answers whether a copy stopped the run, and whether a
    /// cancel stopped the batch before a copy in it.
    /// </summary>
    public (bool Stopped, bool Cancelled) Go(List<CopyRemoval> outcomes, CancellationToken ct)
    {
        try
        {
            List<(ComparedCopy Copy, int Slot)> going = [];

            foreach (var waiting in _waiting)
            {
                if (waiting.Copy.Changed(files) is { } changed)
                {
                    outcomes[waiting.Slot] = changed;
                }
                else
                {
                    going.Add(waiting);
                }
            }

            if (going.Count == 0)
            {
                return (false, false);
            }

            // Each copy is identified as soon as the shell has dealt with it, and one that stops the run
            // stops the batch there, before the shell reaches the next copy.
            var identified = new CopyRemoval?[going.Count];
            var recycled = bin.Recycle(
                [.. going.Select(waiting => DuplicateRemover.BinPath(waiting.Copy.Copy))],
                ct,
                (index, outcome) => (identified[index] = Identified(going[index].Copy, outcome)).StopsTheRun);
            var (stopped, cancelled) = (false, false);

            for (var i = 0; i < going.Count; i++)
            {
                var outcome = recycled[i] is { Cancelled: true } or { Stopped: true }
                    ? DuplicateRemover.NotReached(going[i].Copy.Copy)
                    : identified[i] ?? Identified(going[i].Copy, recycled[i]);

                outcomes[going[i].Slot] = outcome;
                cancelled |= recycled[i].Cancelled;
                stopped |= outcome.StopsTheRun;
            }

            return (stopped, cancelled);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var (copy, _) in _waiting)
        {
            copy.Dispose();
        }

        foreach (var kept in _kept)
        {
            kept.Dispose();
        }

        _waiting.Clear();
        _kept.Clear();
    }

    /// <summary>
    /// What became of <paramref name="compared"/>, as the bin answered and as its handle shows: moved
    /// only where the bin's item is the file that was compared.
    /// </summary>
    private CopyRemoval Identified(ComparedCopy compared, RecycleOutcome recycled)
    {
        var copy = compared.Copy;

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

        if (FileInformation.FinalPathOf(compared.Held.Content) is not { } heldAt)
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

        return files.Describe(binned, copy.Route) switch
        {
            { Description: { } item } when item.Identity == copy.Identity =>
                new CopyRemoval(copy, RemovalCheck.Removed, "Moved to the Recycle Bin."),
            { Description: not null } => receivedAnother,
            _ => new CopyRemoval(copy, RemovalCheck.BinUnconfirmed,
                $"Windows moved '{copy.Path}' to the Recycle Bin, and would not describe the item it put there, so Deguffer "
                + "cannot show it is the file it compared, and stopped. Restore it from the Recycle Bin if it is not the copy you meant to remove."),
        };
    }
}
