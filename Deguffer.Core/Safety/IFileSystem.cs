namespace Deguffer.Core.Safety;

/// <param name="FullName">The entry's path, in whatever form the enumeration returned it.</param>
/// <param name="Length">Size in bytes; zero for a directory, and for a reparse point.</param>
/// <remarks>
/// A struct because these trees run to hundreds of thousands of entries and G4's per-entry
/// overhead dominates: enumeration already allocates a <see cref="FileSystemInfo"/> apiece, and a
/// class here would double that for no benefit.
/// </remarks>
public readonly record struct FileSystemEntry(
    string FullName,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length);

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
    /// The immediate children of <paramref name="directory"/>, materialised so that an enumeration
    /// failure surfaces here rather than part-way through a deletion.
    /// </summary>
    IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory);

    void DeleteFile(string path);

    /// <summary>Removes an empty directory; never recursive, so ordering stays the caller's.</summary>
    void DeleteDirectory(string path);

    /// <summary>
    /// Resets every attribute on the entry, the read-only bit being the one that matters —
    /// package manager caches set it liberally and it is what blocks a delete.
    /// </summary>
    void ClearAttributes(string path);
}

/// <summary>The real filesystem. Stateless, so a single instance serves the process (G5).</summary>
public sealed class WindowsFileSystem : IFileSystem
{
    public static WindowsFileSystem Default { get; } = new();

    private WindowsFileSystem()
    {
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) =>
        new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Select(entry => new FileSystemEntry(
                entry.FullName,
                entry is DirectoryInfo,
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                entry is FileInfo file ? file.Length : 0))
            .ToList();

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public void ClearAttributes(string path) => File.SetAttributes(path, FileAttributes.Normal);
}
