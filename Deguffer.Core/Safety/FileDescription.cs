using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// A file as Windows knows it, whatever its path: the volume it is on and its number there. Two
/// paths with one identity are one file, reached twice or by two of its names (§7.4).
/// </summary>
/// <param name="Volume">The volume's serial number, in the width the identifying call gives it.</param>
/// <param name="File">The file's number on that volume, 128 bits wide because ReFS needs them all.</param>
public readonly record struct FileIdentity(ulong Volume, UInt128 File);

/// <summary>
/// Which call identifies the files of a volume. Decided once a volume, because the two calls give a
/// volume's serial number at different widths, so one file identified by each would read as two.
/// </summary>
internal enum IdentityRoute
{
    /// <summary><c>FileIdInfo</c>: the full serial number and the 128-bit file number.</summary>
    FileId,

    /// <summary><c>GetFileInformationByHandle</c>, for a file system that does not answer <c>FileIdInfo</c>.</summary>
    Legacy,
}

/// <summary>What <see cref="FileInformation.Describe"/> could say about a path.</summary>
public enum FileReadingResult
{
    /// <summary>Windows described the file, and the description is given.</summary>
    Identified,

    /// <summary>Nothing is at the path: Windows said the file or a folder on the way to it is not there.</summary>
    Gone,

    /// <summary>
    /// Windows would not describe it. It may still be there, so this is never read as
    /// <see cref="Gone"/>: a copy taken for gone could be the only one left.
    /// </summary>
    Unreadable,
}

/// <summary>A file as an attributes-only handle describes it, at the moment it was asked.</summary>
/// <param name="Path">Where the handle says the file is, in display form, once the folders on the way are followed.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Allocated">
/// What it occupies on disk, as Windows reports it: less than its length where it is compressed or
/// sparse, and nothing where it is small enough to live in its file record. Space it shares through
/// block cloning or deduplication is counted in full, as Windows reports no share.
/// </param>
/// <param name="Names">How many names it has: more than one where it has hard links.</param>
/// <param name="Modified">Its last-modified time, to the file system's full precision.</param>
/// <param name="Changed">
/// When Windows last recorded a change to it, to its content, its attributes, its security or its
/// name, to the same precision.
/// A program can put the last-modified time back after a write, and a new file can take a deleted
/// one's number with the same length and last-modified time, but neither puts this back, so it is
/// what says a remembered checksum is still the file's.
/// </param>
/// <param name="ReparseTag">The tag of the reparse point the entry itself carries, or zero.</param>
public sealed record FileDescription(
    FileIdentity Identity,
    string Path,
    long Length,
    long Allocated,
    int Names,
    DateTime Modified,
    DateTime Changed,
    FileAttributes Attributes,
    uint ReparseTag)
{
    /// <summary>The bit Microsoft sets in every tag whose reparse point names another place: a link, never a file.</summary>
    private const uint NameSurrogate = 0x2000_0000;

    /// <summary>Whether the path names a folder or a link rather than a file that holds data.</summary>
    public bool IsFile => !Attributes.HasFlag(FileAttributes.Directory) && (ReparseTag & NameSurrogate) == 0;
}

/// <summary>What <see cref="FileInformation.Describe"/> answered, and the description where it had one.</summary>
public readonly record struct FileReading(FileReadingResult Result, FileDescription? Description)
{
    public static FileReading Gone => new(FileReadingResult.Gone, null);

    public static FileReading Unreadable => new(FileReadingResult.Unreadable, null);
}

/// <summary>
/// What <see cref="FileInformation.Hold"/> answered, and the attributes-only handle the file was
/// described through where it was identified, held until this is disposed.
/// </summary>
/// <param name="Handle">The handle, open on the entry itself, or null where the file was not identified.</param>
internal sealed record HeldFile(FileReading Reading, SafeFileHandle? Handle) : IDisposable
{
    public void Dispose() => Handle?.Dispose();
}

/// <summary>One named stream of a file, as <see cref="FileInformation.StreamsOf"/> lists it.</summary>
/// <param name="Name">The stream as Windows names it in a listing: <c>:name:$DATA</c>.</param>
/// <param name="Length">Its length in bytes.</param>
internal readonly record struct NamedStream(string Name, long Length);
