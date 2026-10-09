using System.Buffers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Describes the file at a path through a handle opened for attributes alone, and keeps that handle
/// open, as <see cref="FileInformation.Hold"/> does.
/// </summary>
internal delegate HeldFile PathDescriber(string path, IdentityRoute route);

/// <summary>
/// Opens a file to read its content by its number on the volume a held handle is open on, as
/// <see cref="FileInformation.OpenById"/> does, answering an invalid handle where Windows would not.
/// </summary>
internal delegate SafeFileHandle ContentOpener(SafeFileHandle volume, FileIdentity identity, IdentityRoute route);

/// <summary>Opens a file to read its content by its path, in the form it reaches Windows.</summary>
internal delegate SafeFileHandle PathOpener(string extendedPath);

/// <summary>How much of a file a read covers.</summary>
internal enum ContentPart
{
    /// <summary>The first and last blocks, or the whole file where it is no longer than the two.</summary>
    Ends,

    /// <summary>Every byte.</summary>
    Whole,
}

/// <summary>What became of one read, and the bucket a file left out is counted in.</summary>
internal enum ContentReadResult
{
    Read,
    Gone,
    Unidentified,
    OnlyInTheCloud,
    ReadFailed,
    Changed,
}

/// <summary>What <see cref="ContentReader.Read"/> answered.</summary>
/// <param name="Checksum">The checksum of what was read, where <paramref name="Result"/> is <see cref="ContentReadResult.Read"/>.</param>
internal readonly record struct ContentReading(ContentReadResult Result, ContentChecksum Checksum)
{
    public static ContentReading LeftOut(ContentReadResult result) => new(result, default);
}

/// <summary>
/// Reads one file's content for a duplicate search (§7.4), and only a file that is on this device
/// and is still the file the search identified.
///
/// <para><b>Described before it is opened.</b> A file can go online-only after the search saw it,
/// and opening such a file for its content can download it before any check on that handle runs. So
/// the path is first described through a handle that reads attributes alone and recalls nothing,
/// and a file whose attributes say it is not all here is left out without its content ever being
/// opened. The file must still be the one identified: its identity, length and last-modified time to
/// the tick.</para>
///
/// <para><b>Opened by its number, never through a link.</b> The handle the path was described through
/// is held, and the content is opened by the file's number on that handle's volume
/// (<see cref="FileInformation.OpenById"/>), so no path is walked a second time. A folder or a name
/// on the way replaced by a link after the description cannot send the open to another file, to a
/// share, or to a cloud file that opening would recall.</para>
///
/// <para><b>The path is opened only where no link can be on it.</b> Where Windows will not open a
/// file by its number, the path is opened instead only where the volume answered that it supports no
/// reparse points, such as FAT or exFAT (<see cref="LocalVolume.CannotHoldLinks"/>), and the path
/// starts at that volume's own drive letter. A volume that refused the question may be NTFS, and one
/// reached only through a folder of another volume is reached through that volume's folders, either
/// of which can hold a link. Anywhere else the file is left out as a failed read, never as gone,
/// because the held handle shows it was there.</para>
///
/// <para><b>Held while it is read, and checked through what it is read by.</b> The content is opened
/// sharing only reading, so nobody can write to it while it is read, and the handle is described
/// before the first byte and after the last. A file that is not the identified one, a file that went
/// online-only in between, and a file whose times or attributes changed while it was read (which
/// sharing does not prevent) are each left out. What was read is never taken for the file's on any
/// other evidence.</para>
///
/// <para><b>A checksum read before stands in for reading the bytes</b> where a cache is given
/// (<see cref="ChecksumCache"/>), never for opening them: it is asked only once the content is
/// open and judged as before a read, and kept only from a read that found nothing changed, its
/// change time included.</para>
///
/// <para><b>A refusal is never absence.</b> A file Windows would not describe is counted as
/// unidentified, and one it would not let the search read as a failed read, never as gone, because a
/// copy taken for gone may be the only one left.</para>
/// </summary>
internal sealed class ContentReader
{
    /// <summary>
    /// The size of the first and last blocks. Large enough to hold the parts of a file where formats
    /// differ, the header and the index or trailer at the end, which a 4 KiB sample of a large media
    /// file can share with another. Small enough that reading two costs a spinning disk little more
    /// than the seek it pays anyway: at 150 MB/s, 64 KiB takes under half a millisecond against a
    /// seek of around ten. A file no longer than two blocks is read whole at once, so only larger
    /// files are ever read twice.
    /// </summary>
    public const int BlockBytes = 64 * 1024;

    /// <summary>The size of each read of a whole file, large enough that the disk, not the calls, sets the pace.</summary>
    private const int WholeReadBytes = 1024 * 1024;

    private readonly FileInformation _files;
    private readonly PathDescriber _describe;
    private readonly ContentOpener _open;
    private readonly PathOpener _openPath;
    private readonly ChecksumCache? _remembered;

    public static ContentReader Default { get; } =
        new(FileInformation.Default, FileInformation.Default.Hold, FileInformation.OpenById, OpenPath);

    /// <summary>A reader that uses and keeps the checksums <paramref name="remembered"/> holds.</summary>
    public static ContentReader Remembering(ChecksumCache remembered) =>
        new(FileInformation.Default, FileInformation.Default.Hold, FileInformation.OpenById, OpenPath, remembered);

    /// <param name="files">Describes the opened content through its handle.</param>
    /// <param name="describe">
    /// Describes the path before anything is opened for content, so a test can see the path reach
    /// Windows in its extended form (§6.3), and hand back a real description carrying a recall
    /// attribute, which no ordinary file can be given.
    /// </param>
    /// <param name="open">
    /// Opens the content by the file's number, so a test can fail if a file that must not be read is
    /// opened at all, change the disk between the description and the open, or stand for a volume
    /// that will not open a file by its number.
    /// </param>
    /// <param name="openPath">Opens the content by its path, where no link can exist on the volume.</param>
    /// <param name="remembered">
    /// The checksums earlier searches read, used in place of a read where the file is unchanged and
    /// given each new one, or null to read every file.
    /// </param>
    internal ContentReader(FileInformation files, PathDescriber describe, ContentOpener open, PathOpener openPath, ChecksumCache? remembered = null)
    {
        _files = files;
        _describe = describe;
        _open = open;
        _openPath = openPath;
        _remembered = remembered;
    }

    /// <summary>
    /// Whether a read of <paramref name="part"/> of a file of <paramref name="length"/> bytes covers
    /// every byte, so its checksum is the file's own: the first and last blocks of a file no longer
    /// than the two are the whole file, read once.
    /// </summary>
    public static bool IsWhole(ContentPart part, long length) => part is ContentPart.Whole || length <= 2L * BlockBytes;

    /// <summary>
    /// The checksum of <paramref name="part"/> of <paramref name="file"/>'s content, or the reason it
    /// was left out.
    /// </summary>
    /// <param name="checksum">The running checksum to read into, which is left ready for the next file whatever happens.</param>
    public ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var held = _describe(file.Path, file.Route);

        if (Judge(held.Reading, file, attributes: null) is { } before)
        {
            return ContentReading.LeftOut(before);
        }

        var described = held.Reading.Description!;
        var attributes = described.Attributes;
        var remembers = _remembered is not null && ChecksumCache.Keeps(file);

        if (Open(held.Handle!, file) is not { } content)
        {
            return ContentReading.LeftOut(ContentReadResult.ReadFailed);
        }

        using (content)
        {
            var opened = _files.Describe(content, file.Route);

            if (Judge(opened, file, attributes) is { } refused)
            {
                return ContentReading.LeftOut(refused);
            }

            // Asked only once the content is open and judged, so a remembered value stands in for
            // reading the bytes and never for the open: a file another program holds now, or one
            // this account may not read, is left out as a read would leave it (§7.4).
            if (remembers && _remembered!.Find(opened.Description!, part, checksum.Algorithm) is { } known)
            {
                return new ContentReading(ContentReadResult.Read, known);
            }

            ContentChecksum? read;

            try
            {
                read = Checksum(content, file.Length, part, checksum, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A range another program locked, or a device that failed partway.
                return ContentReading.LeftOut(ContentReadResult.ReadFailed);
            }

            if (read is not { } value)
            {
                return ContentReading.LeftOut(ContentReadResult.Changed);
            }

            var finished = _files.Describe(content, file.Route);

            if (Judge(finished, file, attributes) is { } after)
            {
                return ContentReading.LeftOut(after);
            }

            // Kept only where nothing Windows records about the file changed while it was read, so
            // the value stands for the file in the state its key names.
            if (remembers && finished.Description!.Changed == described.Changed)
            {
                _remembered!.Remember(described, part, value);
            }

            return new ContentReading(ContentReadResult.Read, value);
        }
    }

    /// <summary>
    /// The file's content opened by its number on the volume <paramref name="held"/> is open on, or by
    /// its path where Windows will not open it by number and the volume can hold no link, or null
    /// where it could not be opened. Null is a failed read and never absence: the file was described
    /// through <paramref name="held"/>, which is still open.
    /// </summary>
    private SafeFileHandle? Open(SafeFileHandle held, DuplicateCandidate file)
    {
        var content = _open(held, file.Identity, file.Route);

        if (!content.IsInvalid)
        {
            return content;
        }

        content.Dispose();

        // Another program holding the file without sharing it for reading, or an access rule that
        // refuses its data, is refused by number as it would be by path, so only whether a link can
        // be on the way decides whether the path may be tried.
        if (!file.NoLinkOnItsPath)
        {
            return null;
        }

        try
        {
            return _openPath(LongPath.Extended(file.Path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gone by now, held by another program, refused, or a device that failed: a failed read,
            // for the reason above.
            return null;
        }
    }

    /// <summary>
    /// Why a file as described now must not be read, or have its reading kept, or null where it is
    /// still the file the search identified, on this device.
    /// </summary>
    /// <param name="attributes">The attributes described before the content was opened, which must not have changed since.</param>
    internal static ContentReadResult? Judge(FileReading reading, DuplicateCandidate file, FileAttributes? attributes)
    {
        switch (reading.Result)
        {
            case FileReadingResult.Gone:
                return ContentReadResult.Gone;

            case FileReadingResult.Unreadable:
                return ContentReadResult.Unidentified;
        }

        var description = reading.Description!;

        if (!description.IsFile)
        {
            return ContentReadResult.Gone;
        }

        if (StorageAttributes.Of(description.Attributes) is FileStorage.CloudOnly)
        {
            return ContentReadResult.OnlyInTheCloud;
        }

        if (description.Identity != file.Identity
            || description.Length != file.Length
            || description.Modified != file.Modified
            || (attributes is { } before && description.Attributes != before))
        {
            return ContentReadResult.Changed;
        }

        return null;
    }

    /// <summary>
    /// The checksum of the part read, or null where the file ended before the length it was
    /// identified with.
    /// </summary>
    private static ContentChecksum? Checksum(SafeFileHandle content, long length, ContentPart part, Checksum checksum, CancellationToken ct)
    {
        var whole = IsWhole(part, length);
        var buffer = ArrayPool<byte>.Shared.Rent(whole ? WholeReadBytes : BlockBytes);
        var finished = false;

        try
        {
            var complete = whole
                ? Append(content, 0, length, checksum, buffer, ct)
                : Append(content, 0, BlockBytes, checksum, buffer, ct)
                    && Append(content, length - BlockBytes, BlockBytes, checksum, buffer, ct);

            var value = checksum.Finish();
            finished = true;

            return complete ? value : null;
        }
        finally
        {
            // A read that stopped partway leaves its bytes in the checksum, and the next file read
            // through the same one would take them for its own first bytes.
            if (!finished)
            {
                checksum.Finish();
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Appends <paramref name="count"/> bytes from <paramref name="offset"/>, asking between reads
    /// whether the search was stopped, or answers false where the file ended first.
    /// </summary>
    private static bool Append(SafeFileHandle content, long offset, long count, Checksum checksum, byte[] buffer, CancellationToken ct)
    {
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var span = buffer.AsSpan(0, (int)Math.Min(buffer.Length, count));
            var read = RandomAccess.Read(content, span, offset);

            if (read == 0)
            {
                return false;
            }

            checksum.Append(span[..read]);
            offset += read;
            count -= read;
        }

        return true;
    }

    /// <summary>
    /// Opened as <see cref="FileInformation.OpenById"/> opens by number: sharing only reading, so no
    /// other program can write to the file while it is read, and for reading in order, which tells
    /// Windows to read ahead.
    /// </summary>
    private static SafeFileHandle OpenPath(string extendedPath) =>
        File.OpenHandle(extendedPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
}
