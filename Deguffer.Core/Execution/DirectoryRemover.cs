using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">Bytes of files actually deleted.</param>
/// <param name="Skipped">Entries left in place because something held them (§5.3).</param>
/// <param name="RootRemoved">Whether the target directory itself is gone.</param>
/// <param name="Kept">
/// Files left in place because the user asked for anything touched recently to be left alone.
///
/// Counted apart from <paramref name="Skipped"/> rather than added to it, because the two are
/// different sentences to the reader: a skip is Windows refusing, which the user can act on by
/// closing something, and this is Deguffer honouring a setting they chose. Reporting a deliberate
/// choice as an obstruction would send them looking for a process that is not there.
/// </param>
/// <param name="Spared">
/// Entries the caller named as off limits and this removal did not descend into (§5.3).
///
/// A third sentence again, for the same reason the second is separate: this is neither Windows
/// refusing nor a setting the user chose, but Deguffer declining to touch something it found
/// somebody using. The user acts on it by closing that program, so the count is what tells them
/// there is something to close.
/// </param>
public sealed record RemovalOutcome(
    long BytesReclaimed,
    int Skipped,
    bool RootRemoved,
    int Kept = 0,
    int Spared = 0);

/// <summary>
/// What a removal must leave behind, beyond the guard on recently changed files.
///
/// <para>Both members exist for §5.3's scratch folders, and neither can be expressed as an age.
/// <c>%TEMP%</c> is not a cache Deguffer may take away — every program on the machine expects the
/// folder itself to be there, and Windows does not put it back — so its <em>contents</em> are the
/// subject and the directory is not. And a folder a running program is working in is off limits
/// however old its files are, because the process holding it may have opened nothing this minute.
/// </para>
///
/// <para><b>They travel together because they are one caller's answer to one question.</b> A
/// removal that kept the root but forgot the exclusions would empty a live program's scratch
/// directory, and one that honoured the exclusions but not the root would delete the folder the
/// exclusions were inside. Passing them as one value is what stops half of that arriving.</para>
/// </summary>
/// <param name="KeepRoot">
/// Whether the directory named by the removal must itself survive. False for every ordinary
/// deletion, where the directory is the thing being removed.
/// </param>
/// <param name="Spared">
/// Paths this removal must not delete or descend into, in display form. Compared at every level
/// rather than only against the root's own children, so a caller sparing something nested is
/// honoured rather than silently ignored — the direction §5.2 requires an unrecognised case to fail
/// in.
/// </param>
public sealed record RemovalBounds(bool KeepRoot, IReadOnlyList<string> Spared)
{
    /// <summary>Nothing held back: the tree goes, root included.</summary>
    public static readonly RemovalBounds None = new(KeepRoot: false, []);

    /// <summary>
    /// The spared paths as a set the walk can ask cheaply, in the form
    /// <see cref="Safety.FileSystemEntry.FullName"/> arrives in.
    ///
    /// <para>Built once per removal rather than per entry: the walk asks this of every file and
    /// directory in a tree of hundreds of thousands, and a linear scan of a list there is per-entry
    /// work for an answer a hash lookup gives (G4).</para>
    ///
    /// <para><see cref="LongPath.Extended"/> on the way in, because an enumeration below an extended
    /// root yields extended children, and a set holding display paths would match none of them —
    /// which fails silently and in the dangerous direction: every spared path would be deleted.</para>
    /// </summary>
    internal IReadOnlySet<string> SparedPaths { get; } =
        Spared.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(Spared.Select(LongPath.Extended), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Deletes a directory tree.
///
/// §6.3: deletion is genuinely parallel — these trees are hundreds of thousands of small files,
/// and wall-clock time is dominated by per-file overhead, not bytes. Every path goes through the
/// extended-length prefix, because a MAX_PATH truncation here is a silent partial deletion.
/// </summary>
public static class DirectoryRemover
{
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
            return new RemovalOutcome(0, 0, RootRemoved: !bounds.KeepRoot);
        }

        // The root is the one entry no enumeration classified, so it is the one place a link can
        // still be walked through. Enumerating a junction returns the target's children, which are
        // ordinary directories and files, so Gather would delete a tree nobody looked at and the
        // §5.6 negative — written against paths inside the profile — would pass. Remove the link
        // and stop, exactly as Gather does for a link it finds below.
        //
        // A caller keeping the root removes nothing at all here. Its subject is what is inside the
        // directory, and a link has no inside — following it would empty whatever it points at, and
        // deleting it would destroy the very path the caller said must survive.
        if (fs.IsReparsePoint(extended))
        {
            if (!bounds.KeepRoot)
            {
                TryDeleteDirectory(extended, fs);
            }

            progress?.Report(1.0);

            return new RemovalOutcome(0, 0, RootRemoved: !fs.DirectoryExists(extended));
        }

        // Two passes: gather the tree first so progress is a real fraction rather than a guess,
        // then delete depth-first. Gathering also means a mid-run enumeration failure cannot
        // leave us deleting a partially-understood tree.
        var directories = new List<string>();
        var files = new List<(string Path, long Length)>();
        var (kept, spared) = Gather(extended, keep, bounds, directories, files, fs, ct);

        long reclaimed = 0;
        var skipped = 0;
        var done = 0;
        var total = Math.Max(files.Count, 1);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 4, 32),
        };

        Parallel.ForEach(files, options, file =>
        {
            if (TryDeleteFile(file.Path, fs))
            {
                Interlocked.Add(ref reclaimed, file.Length);
            }
            else
            {
                Interlocked.Increment(ref skipped);
            }

            var completed = Interlocked.Increment(ref done);
            if (completed % 256 == 0 || completed == files.Count)
            {
                progress?.Report((double)completed / total);
            }
        });

        // Deepest first, so a directory is only removed once its children are gone. Ordering by
        // path length is a correct topological order here, not a shortcut: a parent's path is
        // always a strict prefix of its descendants', so it is always strictly shorter.
        // Directories still holding a skipped file simply stay — the correct outcome, not an error.
        foreach (var directory in directories.OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();

            // The root goes last by construction, so skipping it here cannot leave a child behind.
            if (bounds.KeepRoot && directory.Equals(extended, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteDirectory(directory, fs);
        }

        progress?.Report(1.0);

        // The root is in `directories`, so the loop above has already attempted it — unless the
        // caller asked for it to stay, in which case it is still there and that is the success.
        return new RemovalOutcome(
            reclaimed,
            skipped,
            RootRemoved: !bounds.KeepRoot && !fs.DirectoryExists(extended),
            kept,
            spared);
    }

    /// <summary>
    /// Collect the tree, and return how many files the guard held back and how many entries the
    /// bounds did.
    ///
    /// Both exclusions are applied here rather than in the deletion pass because this is where the
    /// evidence already is: the enumeration that classified the entry read its timestamp and its
    /// path, and gathering is also where a file the removal must not touch stops being a candidate
    /// at all. An entry filtered out here is never handed to <see cref="TryDeleteFile"/> or
    /// <see cref="TryDeleteDirectory"/>, so there is no second place holding the same rule.
    /// </summary>
    private static (int Kept, int Spared) Gather(
        string extendedDirectory,
        MinimumAge keep,
        RemovalBounds bounds,
        List<string> directories,
        List<(string, long)> files,
        IFileSystem fs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        directories.Add(extendedDirectory);

        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = fs.EnumerateEntries(extendedDirectory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Unreadable directory: nothing to gather, and §5.3 says skip rather than fail.
            return (0, 0);
        }

        var kept = 0;
        var spared = 0;

        foreach (var entry in entries)
        {
            // Asked before the link branch below, because a spared entry is spared whatever it
            // turns out to be. A scratch directory a running program was handed as a junction is
            // still that program's, and removing the link is still taking it away.
            if (bounds.SparedPaths.Contains(entry.FullName))
            {
                spared++;
                continue;
            }

            if (entry.IsReparsePoint)
            {
                // Never follow a junction or symlink: deletion would escape the target tree.
                // Remove the link itself and stop there.
                if (entry.IsDirectory)
                {
                    TryDeleteDirectory(entry.FullName, fs);
                }
                else
                {
                    TryDeleteFile(entry.FullName, fs);
                }

                continue;
            }

            if (entry.IsDirectory)
            {
                var below = Gather(entry.FullName, keep, bounds, directories, files, fs, ct);
                kept += below.Kept;
                spared += below.Spared;
            }
            else if (keep.Protects(entry.NewestFileTime))
            {
                kept++;
            }
            else
            {
                files.Add((entry.FullName, entry.Length));
            }
        }

        return (kept, spared);
    }

    private static bool TryDeleteFile(string extendedPath, IFileSystem fs)
    {
        try
        {
            fs.DeleteFile(extendedPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Commonly just the read-only bit — package manager caches set it liberally.
            try
            {
                fs.ClearAttributes(extendedPath);
                fs.DeleteFile(extendedPath);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            // Held open by a live process. §5.3: this is the OS protecting state; skip it.
            return false;
        }
    }

    private static void TryDeleteDirectory(string extendedPath, IFileSystem fs)
    {
        try
        {
            fs.DeleteDirectory(extendedPath);
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Not empty, in use, already gone — or the read-only bit, which the retry below is for.
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
        if (fs.TryGetAttributes(extendedPath) is not { } attributes || !attributes.HasFlag(FileAttributes.ReadOnly))
        {
            return;
        }

        // Emptiness is asked only of a real directory, and it costs nothing there: clearing the bit
        // on one that still holds something cannot make the removal succeed, so the only thing it
        // could achieve is changing the attributes of a path this removal is leaving standing. A
        // link is the case the question must not be asked of at all — enumerating it reads the far
        // side, which nothing here has classified. Its own attributes are what get cleared, and
        // removing the link without following it is what the caller asked for.
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !IsEmpty(extendedPath, fs))
        {
            return;
        }

        try
        {
            fs.ClearAttributes(extendedPath);
            fs.DeleteDirectory(extendedPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Held open, or something arrived in it between the two calls. The read-only bit is
            // cleared and the directory stays, which is the same residue TryDeleteFile leaves on the
            // same path — and it is a directory this plan named for removal either way.
        }
    }

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
}
