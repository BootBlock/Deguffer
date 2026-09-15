namespace Deguffer.Core.Safety;

/// <param name="FullName">The entry's path, in whatever form the enumeration returned it.</param>
/// <param name="Length">Size in bytes; zero for a directory, and for a reparse point.</param>
/// <param name="NewestFileTime">
/// The newer of the entry's creation and last-write times, as a FILETIME, for
/// <see cref="MinimumAge"/> to judge. Taken from the enumeration that produced the entry, so it
/// costs no further I/O — and it has to travel with the entry, because a removal that re-read the
/// timestamp would be asking about a file the walk has already classified.
///
/// <para>Zero by default, which is what NTFS writes for a timestamp it never set and what no guard
/// protects. A filesystem implementation that does not answer this leaves every entry deletable,
/// which is the behaviour with the guard off.</para>
/// </param>
/// <remarks>
/// A struct because these trees run to hundreds of thousands of entries and G4's per-entry
/// overhead dominates: enumeration already allocates a <see cref="FileSystemInfo"/> apiece, and a
/// class here would double that for no benefit.
/// </remarks>
public readonly record struct FileSystemEntry(
    string FullName,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    long NewestFileTime = 0);

/// <summary>
/// The filesystem operations a deletion performs, behind an interface so a test can observe the
/// paths that actually reach Win32.
///
/// §6.3 requires every path in Core to carry the extended-length prefix, because a MAX_PATH
/// truncation is a silent partial deletion. That requirement is not observable from the outcome of
/// a deletion: .NET's own path normalisation prepends <c>\\?\</c> to any path of 260 characters or
/// more before it calls Win32, so a deep tree is removed correctly whether or not Core applied the
/// prefix itself. Asserting that a long tree went away therefore proves nothing about Core — it
/// passes identically with the prefixing deleted outright.
///
/// This seam is what makes the requirement testable. A test wraps it and asserts on the *form* of
/// every path handed across, which discriminates on any machine, regardless of the
/// <c>LongPathsEnabled</c> registry value or the host process's manifest.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>
    /// What Windows says is at <paramref name="path"/>, keeping "nothing is there" apart from
    /// "Windows would not say". See <see cref="PathPresence"/>.
    ///
    /// <para>A removal asks this rather than <see cref="DirectoryExists"/> whenever the answer
    /// becomes a claim that the directory went. A refused probe answers false there, and
    /// <see cref="Execution.RemovalOutcome.RootRemoved"/> then reports a directory that is still
    /// standing as one this run took away.</para>
    /// </summary>
    PathPresence ProbeDirectory(string path);

    /// <summary>
    /// Whether <paramref name="path"/> is a junction or symbolic link.
    ///
    /// Asked of the removal root, which is the one entry no enumeration ever classifies: every
    /// other entry arrives through <see cref="EnumerateEntries"/> carrying
    /// <see cref="FileSystemEntry.IsReparsePoint"/> already.
    /// </summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// The immediate children of <paramref name="directory"/>, materialised so that an enumeration
    /// failure surfaces here rather than part-way through a deletion.
    /// </summary>
    IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory);

    /// <summary>
    /// The length of the file at <paramref name="path"/>, or null when no file is there.
    ///
    /// Asked by <see cref="Execution.FileRemover"/>, which deletes a path it was named rather than one it
    /// enumerated, so nothing has told it how large the file is. Null covers both "already gone"
    /// and "something that is not a file has that name", because neither is a thing to delete.
    /// </summary>
    long? TryGetFileLength(string path);

    /// <summary>
    /// The newer of the entry's creation and last-write times as a FILETIME, or null when nothing is
    /// there — the same question <see cref="FileSystemEntry.NewestFileTime"/> answers for an
    /// enumerated entry, asked of a path nothing enumerated.
    ///
    /// <para>Asked by <see cref="Execution.FileRemover"/>, which is handed a single named path. It
    /// re-asks immediately before deleting rather than trusting the plan, because a plan is made
    /// minutes before it runs and the file may have been written to in between — and §7.1 already
    /// settles that a refusal is decided again at the point of deletion rather than only where the
    /// offer was made.</para>
    ///
    /// <para><b>A directory answers too, which the file-only version did not.</b>
    /// <see cref="Execution.DirectoryRemover"/> asks it of a removal root that turns out to be a
    /// junction, and a junction is a directory — so answering null for one meant the guard could not
    /// see it, and a link somebody had made an hour ago was removed under a plan promising nothing
    /// touched in the last eight hours would be. The timestamps are the link's own rather than its
    /// target's, which is the right subject: removing the link is what takes the directory away from
    /// whoever was using it.</para>
    /// </summary>
    long? TryGetNewestFileTime(string path);

    void DeleteFile(string path);

    /// <summary>
    /// Why Windows would refuse to let this process delete the file at <paramref name="path"/> right
    /// now, or null where it would not — or where nothing is there to refuse.
    ///
    /// <para>Nothing is deleted. The file is opened for deletion and closed again, which is the only
    /// question that reaches a filter driver refusing below the ACL. See
    /// <see cref="DeletionProbe"/> for what that costs another program, and why it is asked only
    /// inside the places a previous clean found refused.</para>
    /// </summary>
    RefusalReason? ProbeRemoval(string path);

    /// <summary>Removes an empty directory; never recursive, so ordering stays the caller's.</summary>
    void DeleteDirectory(string path);

    /// <summary>
    /// Resets every attribute on the entry, the read-only bit being the one that matters —
    /// package manager caches set it liberally and it is what blocks a delete.
    /// </summary>
    void ClearAttributes(string path);

    /// <summary>
    /// The entry's attributes, or null when nothing is there.
    ///
    /// Asked before clearing them, so a directory whose removal was refused for some other reason
    /// keeps the attributes it had. Windows reports a read-only directory's refusal as
    /// <see cref="UnauthorizedAccessException"/> for a plain path and as a bare
    /// <see cref="IOException"/> for the extended-length form §6.3 requires, so the exception
    /// discriminates nothing and the attributes themselves are the only honest answer.
    /// </summary>
    FileAttributes? TryGetAttributes(string path);

    /// <summary>
    /// Whether anything — a file, a directory or a link — may be at <paramref name="path"/>.
    ///
    /// <para>False only where Windows says nothing is there. A path that could not be asked about
    /// answers true, which is where this differs from <see cref="TryGetAttributes"/>: the caller is
    /// <see cref="Exploring.Acting.ExploreActionPolicy"/>, asking whether a folder holds something
    /// it refuses to remove, and "I cannot tell" has to read as the answer that stops the removal.</para>
    /// </summary>
    bool MayExist(string path);
}

/// <summary>The real filesystem. Stateless, so a single instance serves the process (G5).</summary>
public sealed class WindowsFileSystem : IFileSystem
{
    public static WindowsFileSystem Default { get; } = new();

    private WindowsFileSystem()
    {
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public PathPresence ProbeDirectory(string path) => LongPath.ProbeDirectory(path);

    public bool IsReparsePoint(string path) => LongPath.IsReparsePoint(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) =>
        new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Select(entry => new FileSystemEntry(
                entry.FullName,
                entry is DirectoryInfo,
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                entry is FileInfo file ? file.Length : 0,
                MinimumAge.NewestFileTimeOf(entry)))
            .ToList();

    public long? TryGetFileLength(string path)
    {
        var file = new FileInfo(path);

        return file.Exists ? file.Length : null;
    }

    public long? TryGetNewestFileTime(string path)
    {
        var file = new FileInfo(path);

        if (file.Exists)
        {
            return MinimumAge.NewestFileTimeOf(file);
        }

        // A directory, which for this question is a junction the removal is about to take. FileInfo
        // reports one as not existing, so asking it alone left the guard blind to exactly the entry
        // whose deletion it was meant to stop.
        var directory = new DirectoryInfo(path);

        return directory.Exists ? MinimumAge.NewestFileTimeOf(directory) : null;
    }

    public void DeleteFile(string path) => File.Delete(path);

    public RefusalReason? ProbeRemoval(string path) => DeletionProbe.Probe(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public void ClearAttributes(string path) => File.SetAttributes(path, FileAttributes.Normal);

    public FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            // Gone, or unreadable. Either way there is nothing here to decide about.
            return null;
        }
    }

    public bool MayExist(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Not an answer, and read as present for the reason LongPath.IsReparsePoint gives.
            return true;
        }
    }
}
