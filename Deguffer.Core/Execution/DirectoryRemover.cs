using System.Collections.Concurrent;
using Deguffer.Core.Safety;

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
        RemovalBounds? bounds = null) =>
        // Not handed the token: Task.Run throws for a token cancelled before the work starts, which
        // would break the contract above at its first step.
        Task.Run(
            () => Remove(
                path, keep, bounds ?? RemovalBounds.None, progress, fileSystem ?? WindowsFileSystem.Default, ct),
            CancellationToken.None);

    private static RemovalOutcome Remove(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IProgress<double>? progress,
        IFileSystem fs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var extended = LongPath.Extended(path);

        switch (fs.ProbeDirectory(extended))
        {
            case PathPresence.Absent:
                // A caller keeping the root is asking about a directory that is meant to still be
                // there, so its absence is not the success it is for a deletion.
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
            case PathPresence.Refused:
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false)
                {
                    LeftStanding = bounds.KeepRoot ? [] : [LongPath.Display(extended)],
                };
        }

        // The root is the one entry no enumeration classified, so it is the one place a link can
        // still be walked through. Enumerating a junction returns the target's children, which are
        // ordinary directories and files, so the walk would gather a tree nobody looked at and the
        // §5.6 negative — written against paths inside the profile — would pass. Remove the link
        // and stop, exactly as the walk does for a link it finds below.
        //
        // A caller keeping the root removes nothing at all here. Its subject is what is inside the
        // directory, and a link has no inside — following it would empty whatever it points at, and
        // deleting it would destroy the very path the caller said must survive.
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
            if (fs.TryGetNewestFileTime(extended) is { } newest && keep.Protects(newest))
            {
                progress?.Report(1.0);

                return new RemovalOutcome(0, Refusals.None, RootRemoved: false, Kept: 1);
            }

            var linkStayed = !bounds.KeepRoot && DirectoryDeletion.TryDelete(extended, fs) is not null;

            progress?.Report(1.0);

            // "Removed" has to mean Windows said it is gone. A refused probe answers false to the
            // two-state question, which would report a link that is still there as one this run took.
            var linkRemoved = fs.ProbeDirectory(extended) is PathPresence.Absent;

            return new RemovalOutcome(
                0,
                Refusals.None,
                RootRemoved: linkRemoved,
                EntriesRemoved: linkRemoved && !bounds.KeepRoot ? 1 : 0)
            {
                LeftStanding = linkStayed ? [LongPath.Display(extended)] : [],
            };
        }

        // Two passes: gather the tree first so progress is a real fraction rather than a guess,
        // then delete depth-first. Gathering also means a mid-run enumeration failure cannot
        // leave us deleting a partially-understood tree.
        RemovalInventory inventory;

        try
        {
            inventory = RemovalWalk.Gather(extended, keep, bounds, fs, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NothingDone;
        }

        var leftStanding = new List<string>();

        // Every entry this removal takes, so a tree of empty folders reports what went rather than
        // nothing: files, links and folders alike.
        long removed = 0;

        // A link is removed as a link and holds no bytes of its own, so a refusal to remove one
        // moves no figure and is not counted. A directory link that stays is still recorded as
        // standing, because what §5.6 reads from it is where the removal went, not what it weighed.
        foreach (var link in inventory.Links)
        {
            var linkPath = link.FullName;
            var gone = link.IsDirectory
                ? DirectoryDeletion.TryDelete(linkPath, fs) is null
                : TryDeleteFile(linkPath, link.Attributes, fs) is null;

            if (gone)
            {
                removed++;
            }
            else if (link.IsDirectory)
            {
                leftStanding.Add(linkPath);
            }
        }

        long reclaimed = 0;
        var done = 0;
        var total = Math.Max(inventory.Files.Count, 1);
        var refused = new RefusalCounter();
        var refusedAt = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = MostAtOnce,
        };

        var directories = inventory.Directories;

        // What became of each directory, by its place in the inventory, and null until it is tried. The
        // pass runs on many threads, so where it stopped is a different place in every branch, and only
        // a record per directory can say which folders a removal stopped part-way never tried.
        var attempts = new Attempt?[directories.Count];
        var interrupted = false;

        try
        {
            Parallel.ForEach(inventory.Files, options, file =>
            {
                var filePath = file.FullName;

                if (TryDeleteFile(filePath, file.Attributes, fs) is { } reason)
                {
                    refused.Add(reason, file.Length);
                    refusedAt.TryAdd(EntryHolding(extended, filePath), 0);
                }
                else
                {
                    Interlocked.Add(ref reclaimed, file.Length);
                    Interlocked.Increment(ref removed);
                }

                var completed = Interlocked.Increment(ref done);
                if (completed % 256 == 0 || completed == inventory.Files.Count)
                {
                    progress?.Report((double)completed / total);
                }
            });

            RemoveDirectories(directories, bounds.KeepRoot, attempts, fs, options);

            progress?.Report(1.0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true;
        }

        var refusedFolders = FolderRefusals.None;

        // Read in reverse of the walk, children before their parent, so the record comes out in one
        // order however the threads interleaved.
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            var directory = directories[index];

            // A root the caller keeps is never tried, so it is neither removed nor left standing.
            if (bounds.KeepRoot && directory.Parent == GatheredDirectory.NoParent)
            {
                continue;
            }

            switch (attempts[index])
            {
                // A removal stopped part-way was inside every folder it had not yet tried: it gathered
                // them, and any file in them may already be gone. Each is recorded as standing for that
                // reason, so §5.6 reads a protected folder among them as one this removal went into. See
                // RunResidue.
                case null:
                    leftStanding.Add(directory.Path);
                    break;

                case { Left: null }:
                    removed++;
                    break;

                // A directory still holding something refused stays, and so does one Windows refuses
                // for itself. Neither is an error (§5.3), and both are recorded: a folder left standing
                // is where this removal went, and only the second is a refusal the reader has not
                // already been told about.
                case { Left: { } standing }:
                    leftStanding.Add(directory.Path);

                    if (standing.Refused is { } reason)
                    {
                        refusedFolders += FolderRefusals.One(reason);
                    }

                    break;
            }
        }

        // The root is among the directories, so the pass above has already attempted it — unless
        // the caller asked for it to stay, in which case it is still there and that is the success.
        return new RemovalOutcome(
            reclaimed,
            refused.Total,
            RootRemoved: !bounds.KeepRoot && fs.ProbeDirectory(extended) is PathPresence.Absent,
            inventory.Kept,
            inventory.Spared.Count,
            Interlocked.Read(ref removed))
        {
            RefusedAt = [.. refusedAt.Keys.Select(LongPath.Display).Order(StringComparer.OrdinalIgnoreCase)],
            LeftStanding = [.. leftStanding.Select(LongPath.Display)],
            RefusedFolders = refusedFolders,
            MailStores = [.. inventory.MailStores.Select(LongPath.Display)],
            LeftAlone = [.. inventory.Spared.Select(LongPath.Display)],
            Interrupted = interrupted,
        };
    }

    /// <summary>
    /// Try each directory once every directory inside it has been tried, and record in
    /// <paramref name="attempts"/> what became of it.
    ///
    /// <para><b>In parallel, as the files are.</b> A tree of a million small files holds a hundred
    /// thousand directories or more, and removing those one at a time on one thread can take longer
    /// than the parallel pass that emptied them.</para>
    ///
    /// <para><b>Children before their parent, without a queue.</b> Each directory with no directory
    /// inside it starts a climb, and the thread that tries a directory's last outstanding child goes on
    /// to try the directory itself. The count of children still outstanding is all the threads share,
    /// so no directory is tried early and none waits for a thread to notice it is ready. Trying a
    /// parent whose child stayed is still right: Windows refuses it as not empty, which is how a folder
    /// held up by what is inside it is recorded.</para>
    /// </summary>
    private static void RemoveDirectories(
        IReadOnlyList<GatheredDirectory> directories,
        bool keepRoot,
        Attempt?[] attempts,
        IFileSystem fs,
        ParallelOptions options)
    {
        var outstanding = new int[directories.Count];
        foreach (var directory in directories)
        {
            if (directory.Parent != GatheredDirectory.NoParent)
            {
                outstanding[directory.Parent]++;
            }
        }

        var leaves = new List<int>();
        for (var index = 0; index < directories.Count; index++)
        {
            if (outstanding[index] == 0)
            {
                leaves.Add(index);
            }
        }

        // Balanced as it goes, because a climb is as long as the chain of last children above its leaf,
        // and a split made up front could leave one thread with every long climb.
        Parallel.ForEach(Partitioner.Create(leaves, loadBalance: true), options, leaf =>
        {
            for (var index = leaf; ; index = directories[index].Parent)
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                var directory = directories[index];

                // The root is the last directory any climb reaches, so skipping it here cannot leave a
                // child behind.
                if (!(keepRoot && directory.Parent == GatheredDirectory.NoParent))
                {
                    attempts[index] = new Attempt(DirectoryDeletion.TryDelete(directory.Path, fs));
                }

                if (directory.Parent == GatheredDirectory.NoParent ||
                    Interlocked.Decrement(ref outstanding[directory.Parent]) != 0)
                {
                    return;
                }
            }
        });
    }

    /// <summary>A removal stopped before it deleted anything.</summary>
    private static RemovalOutcome NothingDone => new(0, Refusals.None, RootRemoved: false) { Interrupted = true };

    /// <summary>
    /// The entry directly inside <paramref name="root"/> that <paramref name="path"/> is at or
    /// below. Both arrive from the same walk in the same extended form, so the prefix is known to
    /// match and only the separator after it has to be found.
    /// </summary>
    private static string EntryHolding(string root, string path)
    {
        // A volume root keeps its separator, and every other root has none after it.
        var start = root.EndsWith(Path.DirectorySeparatorChar) ? root.Length : root.Length + 1;
        var end = path.IndexOf(Path.DirectorySeparatorChar, start);

        return end < 0 ? path : path[..end];
    }

    /// <summary>
    /// Delete one file, and say why Windows would not let it go — or null where it went, or where it
    /// had already gone.
    /// </summary>
    /// <param name="attributes">
    /// The file's attributes as the walk's listing read them. A read-only bit there is cleared before
    /// the delete rather than after Windows refuses it: package manager caches set the bit on every
    /// file, and each refusal is a thrown exception and a second delete.
    /// </param>
    private static RefusalReason? TryDeleteFile(string extendedPath, FileAttributes attributes, IFileSystem fs)
    {
        try
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                fs.ClearAttributes(extendedPath);
            }

            fs.DeleteFile(extendedPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The read-only bit, set since the walk listed the file, or a refusal of another kind
            // that clearing it cannot change and the second attempt reports.
            try
            {
                fs.ClearAttributes(extendedPath);
                fs.DeleteFile(extendedPath);
                return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // It went between the two attempts. Calling that a refusal would report a file
                // Windows kept that is not on the disk at all.
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The second answer is the one that counts: a read-only file something holds open
                // refuses first for the bit and then for the handle.
                return RefusalReasons.Of(ex);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            // Held open by a live process, most often. §5.3: this is the OS protecting state; skip it.
            return RefusalReasons.Of(ex);
        }
    }

    /// <summary>
    /// What became of a directory the removal tried: <paramref name="Left"/> says why it is still
    /// there, and is null where it is gone.
    /// </summary>
    private readonly record struct Attempt(DirectoryDeletion.Standing? Left);
}
