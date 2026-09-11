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
    /// ERROR_DIR_NOT_EMPTY: the answer Windows gives for a folder held up by what is still inside it,
    /// as opposed to one it refused for itself.
    /// </summary>
    private const int ErrorDirectoryNotEmpty = 145;

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
    public static Task<RemovalOutcome> RemoveAsync(
        string path,
        MinimumAge keep = default,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IFileSystem? fileSystem = null,
        RemovalBounds? bounds = null) =>
        Task.Run(
            () => Remove(
                path, keep, bounds ?? RemovalBounds.None, progress, fileSystem ?? WindowsFileSystem.Default, ct),
            ct);

    private static RemovalOutcome Remove(
        string path,
        MinimumAge keep,
        RemovalBounds bounds,
        IProgress<double>? progress,
        IFileSystem fs,
        CancellationToken ct)
    {
        var extended = LongPath.Extended(path);

        if (!fs.DirectoryExists(extended))
        {
            // A caller keeping the root is asking about a directory that is meant to still be
            // there, so its absence is not the success it is for a deletion.
            return new RemovalOutcome(0, Refusals.None, RootRemoved: !bounds.KeepRoot);
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

            var linkStayed = !bounds.KeepRoot && TryDeleteDirectory(extended, fs) is not null;

            progress?.Report(1.0);

            var linkRemoved = !fs.DirectoryExists(extended);

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
        var inventory = RemovalWalk.Gather(extended, keep, bounds, fs, ct);
        var leftStanding = new List<string>();

        // Every entry this removal takes, so a tree of empty folders reports what went rather than
        // nothing: files, links and folders alike.
        long removed = 0;

        // A link is removed as a link and holds no bytes of its own, so a refusal to remove one
        // moves no figure and is not counted. A directory link that stays is still recorded as
        // standing, because what §5.6 reads from it is where the removal went, not what it weighed.
        foreach (var link in inventory.Links)
        {
            var gone = link.IsDirectory
                ? TryDeleteDirectory(link.FullName, fs) is null
                : TryDeleteFile(link.FullName, fs) is null;

            if (gone)
            {
                removed++;
            }
            else if (link.IsDirectory)
            {
                leftStanding.Add(link.FullName);
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
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 4, 32),
        };

        Parallel.ForEach(inventory.Files, options, file =>
        {
            if (TryDeleteFile(file.Path, fs) is { } reason)
            {
                refused.Add(reason, file.Length);
                refusedAt.TryAdd(EntryHolding(extended, file.Path), 0);
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

        var refusedFolders = FolderRefusals.None;

        // Deepest first, so a directory is only removed once its children are gone. Ordering by
        // path length is a correct topological order here, not a shortcut: a parent's path is
        // always a strict prefix of its descendants', so it is always strictly shorter.
        //
        // A directory still holding something refused stays, and so does one Windows refuses for
        // itself. Neither is an error (§5.3), and both are recorded: a folder left standing is where
        // this removal went, and only the second is a refusal the reader has not already been told
        // about.
        foreach (var directory in inventory.Directories.OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();

            // The root goes last by construction, so skipping it here cannot leave a child behind.
            if (bounds.KeepRoot && directory.Equals(extended, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryDeleteDirectory(directory, fs) is not { } standing)
            {
                removed++;
                continue;
            }

            leftStanding.Add(directory);

            if (standing.Refused is { } reason)
            {
                refusedFolders += FolderRefusals.One(reason);
            }
        }

        progress?.Report(1.0);

        // The root is among the directories, so the loop above has already attempted it — unless
        // the caller asked for it to stay, in which case it is still there and that is the success.
        return new RemovalOutcome(
            reclaimed,
            refused.Total,
            RootRemoved: !bounds.KeepRoot && !fs.DirectoryExists(extended),
            inventory.Kept,
            inventory.Spared,
            Interlocked.Read(ref removed))
        {
            RefusedAt = [.. refusedAt.Keys.Select(LongPath.Display).Order(StringComparer.OrdinalIgnoreCase)],
            LeftStanding = [.. leftStanding.Select(LongPath.Display)],
            RefusedFolders = refusedFolders,
        };
    }

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
    private static RefusalReason? TryDeleteFile(string extendedPath, IFileSystem fs)
    {
        try
        {
            fs.DeleteFile(extendedPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Commonly just the read-only bit — package manager caches set it liberally.
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
    /// Delete one empty directory, and say why it is still there — or null where it is gone: removed
    /// here, or already gone by the time this reached it, which is the same post-condition
    /// <see cref="TryDeleteFile"/> reports for a file.
    /// </summary>
    private static Standing? TryDeleteDirectory(string extendedPath, IFileSystem fs)
    {
        Exception refusal;

        try
        {
            fs.DeleteDirectory(extendedPath);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not empty, in use, refused — or the read-only bit, which the retry below is for.
            refusal = ex;
        }

        // Windows refuses to remove a directory carrying the read-only attribute exactly as it
        // refuses a read-only file, so the file path's retry belongs here too. Without it every
        // file inside such a directory goes, the directory stays, and the step still reports
        // success because bytes were reclaimed — leaving a folder the user was told would go.
        //
        // Which refusal happened is not readable from the exception: .NET reports the same
        // read-only directory as UnauthorizedAccessException for a plain path and as a bare
        // IOException for the extended-length form §6.3 requires, and the HResult goes generic with
        // it. The attributes are the only honest answer, so they are read rather than guessed at.
        // Reading them is also what keeps this away from a link: clearing attributes through a
        // reparse point would act on the far side, which nothing here has classified.
        //
        // Attributes that cannot be read belong to a directory still there, most likely, since the
        // deletion above did not report it missing. So the refusal stands as it was given.
        if (fs.TryGetAttributes(extendedPath) is not { } attributes || !attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return StandingAfter(refusal);
        }

        // Emptiness is asked only of a real directory, and it costs nothing there: clearing the bit
        // on one that still holds something cannot make the removal succeed, so the only thing it
        // could achieve is changing the attributes of a path this removal is leaving standing. A
        // link is the case the question must not be asked of at all — enumerating it reads the far
        // side, which nothing here has classified. Its own attributes are what get cleared, and
        // removing the link without following it is what the caller asked for.
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !IsEmpty(extendedPath, fs))
        {
            return Standing.HeldUp;
        }

        try
        {
            fs.ClearAttributes(extendedPath);
            fs.DeleteDirectory(extendedPath);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Held open, or something arrived in it between the two calls. The read-only bit is
            // cleared and the directory stays, which is the same residue TryDeleteFile leaves on the
            // same path — and it is a directory this plan named for removal either way.
            return StandingAfter(ex);
        }
    }

    /// <summary>
    /// What a refusal to remove a directory says about why it stayed.
    ///
    /// <para>Windows answers a folder that still holds something with an error of its own, so that
    /// case is told apart from a folder refused for itself. Observed on Windows 11 for the
    /// extended-length form §6.3 requires: ERROR_DIR_NOT_EMPTY for a folder holding a folder, a
    /// sharing violation for a folder another process has as its working directory, and a bare
    /// IOException for a folder the account is denied.</para>
    /// </summary>
    private static Standing StandingAfter(Exception refusal) =>
        refusal is IOException io && (io.HResult & 0xFFFF) == ErrorDirectoryNotEmpty
            ? Standing.HeldUp
            : new Standing(RefusalReasons.Of(refusal));

    /// <summary>
    /// Whether the directory holds nothing. Asked only of a directory already known not to be a
    /// link, so the enumeration cannot resolve through one.
    /// </summary>
    private static bool IsEmpty(string extendedPath, IFileSystem fs)
    {
        try
        {
            return fs.EnumerateEntries(extendedPath).Count == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Unreadable or already gone: neither is a directory to go on clearing attributes on.
            return false;
        }
    }

    /// <summary>
    /// Why a directory the removal tried to take is still there: the reason Windows gave where it
    /// refused the folder itself, and none where something still inside the folder held it up.
    /// </summary>
    private readonly record struct Standing(RefusalReason? Refused)
    {
        public static Standing HeldUp => new(Refused: null);
    }
}
