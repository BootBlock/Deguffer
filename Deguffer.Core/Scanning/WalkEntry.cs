using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One child of a directory the walk listed, holding only what the walk's callers read of it.
///
/// <para>A value rather than the <see cref="FileSystemInfo"/> the walk once handed back. A walk of a
/// volume meets millions of entries, and building an object and a full path for each was most of what
/// it allocated, for callers that read a name, a length and two timestamps. The full path is built
/// only where it is asked for: for a directory the walk descends into, and for an entry a caller
/// keeps.</para>
/// </summary>
/// <param name="Directory">
/// The directory the entry was listed in, in the extended form §6.3 requires, so
/// <see cref="FullName"/> carries the prefix too. Every entry of one listing shares the one string.
/// </param>
/// <param name="Length">The file's length in bytes, and zero for a directory.</param>
internal readonly record struct WalkEntry(
    string Directory,
    string Name,
    FileAttributes Attributes,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc)
{
    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

    /// <summary>A junction, a symbolic link, or a file carrying any other reparse point.</summary>
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The entry's path, built on each call. Ask once and keep it.</summary>
    public string FullName => Path.Join(Directory, Name);

    /// <summary>The one number <see cref="MinimumAge"/> judges, from the timestamps the listing read.</summary>
    public long NewestFileTime => MinimumAge.NewestFileTimeOf(CreationTimeUtc, LastWriteTimeUtc);

    /// <summary>
    /// The entry the listing is positioned on. Everything here was read by the call that listed the
    /// directory, so this costs no further I/O.
    /// </summary>
    public static WalkEntry Of(string directory, ref System.IO.Enumeration.FileSystemEntry entry) => new(
        directory,
        entry.FileName.ToString(),
        entry.Attributes,
        entry.IsDirectory ? 0 : entry.Length,
        entry.CreationTimeUtc.UtcDateTime,
        entry.LastWriteTimeUtc.UtcDateTime);
}
