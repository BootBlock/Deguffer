using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Deletes a directory tree, as <see cref="TreeRemoval"/> lists it.
///
/// §6.3: deletion is genuinely parallel — these trees are hundreds of thousands of small files,
/// and wall-clock time is dominated by per-file overhead, not bytes. Every path goes through the
/// extended-length prefix, because a MAX_PATH truncation here is a silent partial deletion.
/// </summary>
public static class DirectoryRemover
{
    /// <summary>
    /// The most deletions a removal runs at once, files and folders alike. Each is a short wait on
    /// the disk rather than work for a processor, so it runs several to a processor, up to a ceiling
    /// that keeps one removal from flooding the disk's queue. The removals of one plan that run at once
    /// share it rather than each taking it: see <see cref="ThreadAllowance"/>.
    /// </summary>
    internal static readonly int MostAtOnce = Math.Min(Environment.ProcessorCount * 4, 32);

    /// <param name="fileSystem">
    /// Defaults to the real filesystem. Injectable so a test can assert that every path crossing
    /// the boundary is in extended-length form — see <see cref="IFileSystem"/> for why the outcome
    /// of a removal cannot prove that on its own.
    /// </param>
    /// <param name="keep">
    /// Files the user has asked to be left alone because they were touched recently. A directory
    /// still holding one is then left standing by the existing rule that an unempty directory
    /// cannot be removed — the same outcome a locked file already produces — so nothing here has to
    /// reason about ancestors.
    /// </param>
    /// <param name="bounds">
    /// What this removal must leave behind besides that: the directory itself, and any entry
    /// something is using. <see cref="RemovalBounds.None"/> by default, which is every ordinary
    /// deletion and is what a caller that says nothing gets.
    /// </param>
    /// <param name="plannedEntries">
    /// How many entries the plan measured the tree at (<see cref="ScanSize.Entries"/>), which
    /// <paramref name="progress"/> is a fraction of until the walk has counted the tree. Zero where the
    /// caller has no plan, or reports no progress.
    /// </param>
    /// <param name="ct">
    /// Stops the removal where it is. <b>Never thrown:</b> a cancelled removal returns what it did,
    /// with <see cref="RemovalOutcome.Interrupted"/> set. What a removal stopped part-way has already
    /// taken, and where it went, is what §5.6 needs afterwards, and an exception carries neither.
    /// </param>
    /// <param name="time">
    /// The clock <see cref="RemovalOutcome.Passes"/> is measured on. Defaults to the system clock. The
    /// executor hands over its own, so a step and the passes inside it are read from one clock.
    /// </param>
    /// <param name="deletions">
    /// Where each thread that deletes takes its place, shared with the removals that run beside this
    /// one: see <see cref="ThreadAllowance"/>. Null gives the removal an allowance of
    /// <see cref="MostAtOnce"/> of its own.
    /// </param>
    public static Task<RemovalOutcome> RemoveAsync(
        string path,
        MinimumAge keep = default,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IFileSystem? fileSystem = null,
        RemovalBounds? bounds = null,
        TimeProvider? time = null,
        long plannedEntries = 0,
        ThreadAllowance? deletions = null) =>
        // Not handed the token: a task handed one is cancelled where the token is before the work
        // starts, which would break the contract above at its first step. On a thread of its own,
        // because the walk holds the thread that drives it for the whole removal.
        WorkerThreads.Shared.RunAsync(
            () => Remove(
                path,
                keep,
                bounds ?? RemovalBounds.None,
                progress,
                plannedEntries,
                fileSystem ?? WindowsFileSystem.Default,
                time ?? TimeProvider.System,
                deletions ?? new ThreadAllowance(MostAtOnce),
                ct),
            CancellationToken.None);

    /// <summary>
    /// Delete what <paramref name="gathered"/> holds, on the terms of
    /// <see cref="RemoveAsync(string, MinimumAge, IProgress{double}?, CancellationToken, IFileSystem?, RemovalBounds?, TimeProvider?, long, ThreadAllowance?)"/>,
    /// whose cancellation is never thrown either, with its passes timed on <paramref name="time"/> and
    /// its deletions counted in <paramref name="deletions"/>.
    /// </summary>
    internal static Task<RemovalOutcome> DeleteAsync(
        GatheredRemoval gathered,
        IProgress<double>? progress,
        IFileSystem fs,
        TimeProvider time,
        ThreadAllowance deletions,
        CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync(() => Delete(gathered, progress, fs, time, deletions, ct), CancellationToken.None);

    private static RemovalOutcome Remove(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IProgress<double>? progress,
        long plannedEntries,
        IFileSystem fs,
        TimeProvider time,
        ThreadAllowance deletions,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var extended = LongPath.Extended(path);

        if (GatheredRemoval.Probe(extended, keep, bounds, fs) is { } notATree)
        {
            return Delete(notATree, progress, fs, time, deletions, ct);
        }

        // Deleted while it is listed: each folder's files as soon as its own listing is complete. See
        // TreeRemoval.
        var root = new RemovalFolder(extended, parent: null);

        return TreeRemoval.Run(
            root,
            bounds,
            (listed, stop) => RemovalWalk.Visit(root, keep, bounds, fs, WalkTuning.Default, listed, stop),
            progress,
            plannedEntries,
            fs,
            time,
            gathered: null,
            deletions,
            ct);
    }

    private static RemovalOutcome Delete(
        GatheredRemoval gathered,
        IProgress<double>? progress,
        IFileSystem fs,
        TimeProvider time,
        ThreadAllowance deletions,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var (extended, bounds) = (gathered.Extended, gathered.Bounds);

        switch (gathered)
        {
            // Handed over folder by folder from what the walk gathered, which lists nothing again. The
            // walk that gathered it is the removal's walk.
            case GatheredRemoval.Tree tree:
                return TreeRemoval.Run(
                    tree.Root,
                    bounds,
                    (listed, stop) =>
                    {
                        foreach (var folder in tree.Root.InWalkOrder())
                        {
                            stop.ThrowIfCancellationRequested();
                            listed((RemovalFolder)folder);
                        }
                    },
                    progress,
                    planned: 0,
                    fs,
                    time,
                    tree.Walk,
                    deletions,
                    ct);

            case GatheredRemoval.NotAFolder:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false) { NotADirectory = true };

            // A caller keeping the root is asking about a directory that is meant to still be there,
            // so its absence is not the success it is for a deletion.
            case GatheredRemoval.Gone:
                return new RemovalOutcome(0, Refusals.None, RootRemoved: !bounds.KeepRoot);

            // Windows would not say whether the directory is there, which the two-state question
            // this replaced answered as "gone" — so a removal that reached nothing at all reported
            // the root as taken, and a step the executor then called successful.
            //
            // Recorded as standing only where this removal would have taken it. LeftStanding means
            // "tried and still here", and a caller keeping the root never tries it — so naming it
            // would report a refusal of a deletion nobody attempted. No folder refusal is counted
            // either: PathPresence does not carry the exception, so "Windows would not let Deguffer
            // remove it" would be a guess between an access rule and a link it will not follow. It is
            // named as unlisted whichever it was, because nobody can say what it holds.
            case GatheredRemoval.Refused:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false)
                {
                    LeftStanding = bounds.KeepRoot ? [] : [LongPath.Display(extended)],
                    Unlisted = [LongPath.Display(extended)],
                };

            case GatheredRemoval.Link { IsKept: true }:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false, Kept: 1);

            // A caller keeping the root removes nothing at all here. Its subject is what is inside the
            // directory, and a link has no inside — following it would empty whatever it points at, and
            // deleting it would destroy the very path the caller said must survive.
            case GatheredRemoval.Link:
                // One deletion, which counts toward the bound the removals beside this one share as a
                // crew's deletions do. A removal cancelled while it waits for a place has done nothing.
                if (!bounds.KeepRoot && !deletions.Take(ct))
                {
                    return NothingDone;
                }

                bool linkStayed;

                try
                {
                    linkStayed = !bounds.KeepRoot && DirectoryDeletion.TryDelete(extended, fs) is not null;
                }
                finally
                {
                    if (!bounds.KeepRoot)
                    {
                        deletions.Return();
                    }
                }

                progress?.Report(1.0);

                // "Removed" has to mean Windows said it is gone. A refused probe answers false to the
                // two-state question, which would report a link that is still there as one this run took.
                var linkRemoved = fs.ProbeEntry(extended) is PathPresence.Absent;

                return new RemovalOutcome(
                    0,
                    Refusals.None,
                    RootRemoved: linkRemoved,
                    EntriesRemoved: linkRemoved && !bounds.KeepRoot ? 1 : 0)
                {
                    LeftStanding = linkStayed ? [LongPath.Display(extended)] : [],
                };

            default:
                throw new InvalidOperationException($"A removal gathered as {gathered.GetType().Name} has no deletion.");
        }
    }

    /// <summary>A removal stopped before it deleted anything.</summary>
    private static RemovalOutcome NothingDone => new(0, Refusals.None, RootRemoved: false) { Interrupted = true };
}
