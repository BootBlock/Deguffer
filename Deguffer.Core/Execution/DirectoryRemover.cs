using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Deletes a directory tree.
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
    /// that keeps one removal from flooding the disk's queue.
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
    public static Task<RemovalOutcome> RemoveAsync(
        string path,
        MinimumAge keep = default,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IFileSystem? fileSystem = null,
        RemovalBounds? bounds = null,
        long plannedEntries = 0) =>
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
                ct),
            CancellationToken.None);

    /// <summary>
    /// Gather every tree in <paramref name="paths"/> as its removal would, off the calling thread, and
    /// delete nothing. Each is then removed by <see cref="DeleteAsync"/>, which lists nothing again.
    /// </summary>
    /// <exception cref="OperationCanceledException">The clean was cancelled before every tree was gathered.</exception>
    internal static Task<IReadOnlyList<GatheredRemoval>> GatherAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        IFileSystem fs,
        CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync<IReadOnlyList<GatheredRemoval>>(
            () => [.. paths.Select(path => Gather(path, keep, RemovalBounds.None, fs, ct))],
            ct);

    /// <summary>
    /// Delete what <paramref name="gathered"/> holds, on the terms of
    /// <see cref="RemoveAsync(string, MinimumAge, IProgress{double}?, CancellationToken, IFileSystem?, RemovalBounds?)"/>,
    /// whose cancellation is never thrown either.
    /// </summary>
    internal static Task<RemovalOutcome> DeleteAsync(
        GatheredRemoval gathered,
        IProgress<double>? progress,
        IFileSystem fs,
        CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync(() => Delete(gathered, progress, fs, ct), CancellationToken.None);

    private static RemovalOutcome Remove(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IProgress<double>? progress,
        long plannedEntries,
        IFileSystem fs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var extended = LongPath.Extended(path);

        if (Probe(extended, keep, bounds, fs) is { } notATree)
        {
            return Delete(notATree, progress, fs, ct);
        }

        var root = new RemovalFolder(extended, parent: null);

        return TreeRemoval.Run(
            root,
            bounds,
            (listed, stop) => RemovalWalk.Visit(root, keep, bounds, fs, WalkTuning.Default, listed, stop),
            progress,
            plannedEntries,
            fs,
            ct);
    }

    /// <summary>
    /// What stands at <paramref name="path"/>, and for a directory, everything its removal would act
    /// on. Nothing is deleted.
    /// </summary>
    /// <exception cref="OperationCanceledException">The walk was cancelled.</exception>
    internal static GatheredRemoval Gather(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IFileSystem fs,
        CancellationToken ct)
    {
        var extended = LongPath.Extended(path);

        if (Probe(extended, keep, bounds, fs) is { } notATree)
        {
            return notATree;
        }

        // Walked to its end before anything is deleted, so the removal can be judged on everything it
        // would act on. Deleted from what it gathered: see DeleteAsync.
        var root = new RemovalFolder(extended, parent: null);
        RemovalWalk.Visit(root, keep, bounds, fs, WalkTuning.Default, static _ => { }, ct);

        return new GatheredRemoval.Tree(extended, bounds, root);
    }

    /// <summary>
    /// What stands at <paramref name="extended"/> where it is not a directory to walk: nothing, a path
    /// Windows will not describe, a file, or a link. Null for a directory.
    /// </summary>
    private static GatheredRemoval? Probe(string extended, MinimumAge keep, RemovalBounds bounds, IFileSystem fs)
    {
        var presence = fs.ProbeDirectory(extended);

        // The directory probe answers Absent for a file, so absence is decided by the entry probe. A
        // file that has taken the directory's name since the preview is nothing this step planned to
        // remove, and reading it as the directory gone reported "Removed." for a path still occupied —
        // and let the directory an index belongs to go while a file stood where the index was.
        if (presence is PathPresence.Absent)
        {
            presence = fs.ProbeEntry(extended);

            if (presence is PathPresence.Present)
            {
                return new GatheredRemoval.NotAFolder(extended, bounds);
            }
        }

        switch (presence)
        {
            case PathPresence.Absent:
                return new GatheredRemoval.Gone(extended, bounds);

            case PathPresence.Refused:
                return new GatheredRemoval.Refused(extended, bounds);
        }

        // The root is the one entry no enumeration classified, so it is the one place a link can
        // still be walked through. Enumerating a junction returns the target's children, which are
        // ordinary directories and files, so the walk would gather a tree nobody looked at and the
        // §5.6 negative — written against paths inside the profile — would pass. The link is removed
        // and nothing behind it, exactly as the walk does for a link it finds below.
        if (fs.IsReparsePoint(extended))
        {
            // The guard covers the root exactly as the walk covers a link below it, and for the same
            // reason: a junction carries its own timestamps, so one made an hour ago is an hour old
            // whatever it points at. Without this a cache directory somebody had just relocated with
            // mklink was removed under a plan promising nothing touched in the last eight hours
            // would be — silently, because a link's length is zero and the outcome reported success.
            //
            // Asked of the path rather than of an enumerated entry, because a root is the one thing
            // no enumeration classified. FileRemover re-asks the same question the same way.
            var kept = fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest);

            return new GatheredRemoval.Link(extended, bounds, kept);
        }

        return null;
    }

    private static RemovalOutcome Delete(
        GatheredRemoval gathered,
        IProgress<double>? progress,
        IFileSystem fs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var (extended, bounds) = (gathered.Extended, gathered.Bounds);

        switch (gathered)
        {
            // Handed over folder by folder from what the walk gathered, which lists nothing again.
            case GatheredRemoval.Tree tree:
                return TreeRemoval.Run(
                    tree.Root,
                    bounds,
                    (listed, stop) =>
                    {
                        foreach (var (folder, _) in tree.Root.InWalkOrder())
                        {
                            stop.ThrowIfCancellationRequested();
                            listed((RemovalFolder)folder);
                        }
                    },
                    progress,
                    planned: 0,
                    fs,
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
            // remove it" would be a guess between an access rule and a link it will not follow.
            case GatheredRemoval.Refused:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false)
                {
                    LeftStanding = bounds.KeepRoot ? [] : [LongPath.Display(extended)],
                };

            case GatheredRemoval.Link { IsKept: true }:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false, Kept: 1);

            // A caller keeping the root removes nothing at all here. Its subject is what is inside the
            // directory, and a link has no inside — following it would empty whatever it points at, and
            // deleting it would destroy the very path the caller said must survive.
            case GatheredRemoval.Link:
                var linkStayed = !bounds.KeepRoot && DirectoryDeletion.TryDelete(extended, fs) is not null;

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
