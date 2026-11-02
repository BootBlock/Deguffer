using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// <paramref name="inner"/> inside <paramref name="root"/>, and a filesystem with nothing on it
/// (<see cref="EmptyFileSystem"/>) everywhere else.
///
/// <para>For a fixture whose tests mix the two kinds of path: a tree built for real under the
/// test's own directory, which the policy has to read as it stands, and one invented at a letter
/// such as <c>C:\</c>, which no reading may reach because the machine running the suite keeps its own
/// folders there.</para>
/// </summary>
/// <param name="root">The test's own directory, in display form.</param>
public sealed class ConfinedFileSystem(IFileSystem inner, string root) : IFileSystem
{
    /// <summary>
    /// The root as given, with its 8.3 aliases expanded, and as its alias, because the temporary
    /// folder can be named either way while the policy asks about the other. Spelled from the root
    /// rather than by expanding each path asked about, which would read the disk outside the root.
    /// </summary>
    private readonly string[] _roots =
    [
        .. new[] { root, LongPath.Unaliased(root), ShortPath.Of(root) ?? root }.Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    public bool DirectoryExists(string path) => For(path).DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => For(path).ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => For(path).ProbeEntry(path);

    public bool IsReparsePoint(string path) => For(path).IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        For(directory).ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => For(path).TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => For(path).TryGetNewestFileTime(path);

    public void DeleteFile(string path) => For(path).DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => For(path).ProbeRemoval(path);

    public void DeleteDirectory(string path) => For(path).DeleteDirectory(path);

    public void ClearAttributes(string path) => For(path).ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => For(path).TryGetAttributes(path);

    public bool MayExist(string path) => For(path).MayExist(path);

    public string? FinalPath(string path) => For(path).FinalPath(path);

    /// <summary>
    /// <paramref name="inner"/> for a path inside the root, in either form <see cref="LongPath"/>
    /// produces, and the empty filesystem for any other.
    /// </summary>
    private IFileSystem For(string path)
    {
        var shown = LongPath.Display(path);

        return _roots.Any(known => LongPath.Contains(known, shown)) ? inner : EmptyFileSystem.Instance;
    }
}
