using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One child of a directory the walk listed, holding only what the walk's callers read of it.
///
/// <para>A value rather than the <see cref="FileSystemInfo"/> the walk once handed back. A walk of a
/// volume meets millions of entries, and building an object and a full path for each was most of what
/// it allocated, for callers that read a name, a length and two timestamps. The full path is built
/// only where it is asked for: for a directory the walk descends into, and for an entry a caller
/// keeps. A file's name is built only where the walk asked for it (<see cref="FileNames"/>).</para>
///
/// <para><b>A file listed without a name has none, never an empty one.</b> An empty name would make
/// <see cref="FullName"/> the parent directory's path, which in code that deletes paths is the
/// directory rather than the file. Asking such an entry for <see cref="Name"/> or
/// <see cref="FullName"/> throws instead.</para>
/// </summary>
public readonly record struct WalkEntry
{
    private readonly string? _name;

    /// <param name="directory">
    /// The directory the entry was listed in, in the extended form §6.3 requires, so
    /// <see cref="FullName"/> carries the prefix too. Every entry of one listing shares the one string.
    /// </param>
    /// <param name="name">The entry's name, or null for a file the listing kept without one.</param>
    /// <param name="length">The file's length in bytes, and zero for a directory.</param>
    public WalkEntry(
        string directory,
        string? name,
        FileAttributes attributes,
        long length,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc)
    {
        // The walk enters a directory by its name, so one without a name is a listing gone wrong.
        if (name is null && attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ArgumentException("A directory is always listed with its name.", nameof(name));
        }

        Directory = directory;
        _name = name;
        Attributes = attributes;
        Length = length;
        CreationTimeUtc = creationTimeUtc;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public string Directory { get; }

    /// <summary>The entry's name. Throws for a file the listing kept without one: see <see cref="HasName"/>.</summary>
    public string Name => _name ?? throw new InvalidOperationException(
        $"A file in {Directory} was listed without its name, so it cannot be named or found by its path.");

    /// <summary>Whether the listing kept the entry's name, which it always does for a directory.</summary>
    public bool HasName => _name is not null;

    public FileAttributes Attributes { get; }

    public long Length { get; }

    public DateTime CreationTimeUtc { get; }

    public DateTime LastWriteTimeUtc { get; }

    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

    /// <summary>A junction, a symbolic link, or a file carrying any other reparse point.</summary>
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Whether this is an Outlook data file (§9), whatever mark it carries. Answered for a file listed
    /// without a name too: every listing that keeps files names each store (<see cref="FileNames"/>).
    /// </summary>
    public bool IsMailStore => !IsDirectory && _name is not null && MailStore.Is(_name);

    /// <summary>The entry's path, built on each call. Ask once and keep it. Throws as <see cref="Name"/> does.</summary>
    public string FullName => Path.Join(Directory, Name);

    /// <summary>The one number <see cref="MinimumAge"/> judges, from the timestamps the listing read.</summary>
    public long NewestFileTime => MinimumAge.NewestFileTimeOf(CreationTimeUtc, LastWriteTimeUtc);

    // Written by hand because the generated one reads every property, and Name throws for a nameless file.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Directory = {Directory}, Name = {_name ?? "(not kept)"}, Attributes = {Attributes}, ");
        builder.Append($"Length = {Length}, CreationTimeUtc = {CreationTimeUtc:O}, LastWriteTimeUtc = {LastWriteTimeUtc:O}");
        return true;
    }
}
