using Deguffer.Core.Scanning;

namespace Deguffer.Core.Safety;

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
    /// other entry arrives through <see cref="ListEntries"/> carrying
    /// <see cref="WalkEntry.IsReparsePoint"/> already.
    /// </summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// List the immediate children of <paramref name="directory"/> into <paramref name="into"/>, and
    /// say how the listing ended rather than throwing: a refused or vanished folder is ordinary
    /// (§5.3), and a removal of a large tree meets many. Entries listed before a failure stay in
    /// <paramref name="into"/>, and whether to act on them is the caller's decision.
    ///
    /// <para>The shape of <see cref="ListDirectory"/>, so a removal walks through this seam on the
    /// walk every scan takes.</para>
    /// </summary>
    /// <param name="directory">In the extended form (§6.3), so every entry carries the prefix too.</param>
    ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into);

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
    /// there — the same question <see cref="WalkEntry.NewestFileTime"/> answers for an
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

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        DirectoryQuery.List(directory, request, into);

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

    // Both extend the path themselves, as File.GetAttributes did: a caller behind this seam may hand
    // over either form.
    public FileAttributes? TryGetAttributes(string path) =>
        // Gone, or unreadable. Either way there is nothing here to decide about.
        FileAttributeRead.Read(LongPath.Extended(path), out var attributes) == 0 ? attributes : null;

    public bool MayExist(string path)
    {
        string extended;

        try
        {
            extended = LongPath.Extended(path);
        }
        catch (ArgumentException)
        {
            // Not an answer, and read as present for the reason LongPath.IsReparsePoint gives.
            return true;
        }

        return FileAttributeRead.Read(extended, out _)
            is not (FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound);
    }
}
