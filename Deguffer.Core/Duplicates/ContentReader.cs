using System.Buffers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Duplicates;

/// <summary>Describes the file at a path through a handle opened for attributes alone, as <see cref="FileInformation.Describe(string, IdentityRoute)"/> does.</summary>
internal delegate FileReading PathDescriber(string path, IdentityRoute route);

/// <summary>Opens a file to read its content, given its path in the form it reaches Windows.</summary>
internal delegate SafeFileHandle ContentOpener(string extendedPath);

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
/// <param name="Whole">Whether the read covered every byte, so the checksum is the file's own.</param>
internal readonly record struct ContentReading(ContentReadResult Result, ContentChecksum Checksum, bool Whole)
{
    public static ContentReading LeftOut(ContentReadResult result) => new(result, default, Whole: false);
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
/// <para><b>Held while it is read, and checked through what it is read by.</b> The content is opened
/// sharing only reading, so nobody can write to it while it is read, and the handle is described
/// before the first byte and after the last. A path that names another file by the time it is
/// opened, a file that went online-only in between, and a file whose times or attributes changed
/// while it was read (which sharing does not prevent) are each left out. What was read is never
/// taken for the file's on any other evidence.</para>
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

    public static ContentReader Default { get; } = new(FileInformation.Default, FileInformation.Default.Describe, OpenForContent);

    /// <param name="files">Describes the opened content through its handle.</param>
    /// <param name="describe">
    /// Describes the path before anything is opened for content, so a test can hand back a real
    /// description carrying a recall attribute, which no ordinary file can be given.
    /// </param>
    /// <param name="open">
    /// Opens the content, so a test can see the path reach Windows in its extended form (§6.3), and
    /// fail if a file that must not be read is opened at all.
    /// </param>
    internal ContentReader(FileInformation files, PathDescriber describe, ContentOpener open)
    {
        _files = files;
        _describe = describe;
        _open = open;
    }

    /// <summary>Whether a file of <paramref name="length"/> bytes is read whole by its first and last blocks.</summary>
    public static bool EndsAreWhole(long length) => length <= 2L * BlockBytes;

    /// <summary>
    /// The checksum of <paramref name="part"/> of <paramref name="file"/>'s content, or the reason it
    /// was left out.
    /// </summary>
    /// <param name="checksum">The running checksum to read into, which is left ready for the next file whatever happens.</param>
    public ContentReading Read(DuplicateCandidate file, ContentPart part, Checksum checksum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var described = _describe(file.Path, file.Route);

        if (Judge(described, file, attributes: null) is { } before)
        {
            return ContentReading.LeftOut(before);
        }

        var attributes = described.Description!.Attributes;
        SafeFileHandle content;

        try
        {
            content = _open(LongPath.Extended(file.Path));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ContentReading.LeftOut(ContentReadResult.Gone);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another program holding the file without sharing it for reading, an access rule that
            // refuses its data, or a device that failed: the file may well still be there.
            return ContentReading.LeftOut(ContentReadResult.ReadFailed);
        }

        using (content)
        {
            if (Judge(_files.Describe(content, file.Route), file, attributes) is { } opened)
            {
                return ContentReading.LeftOut(opened);
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

            if (Judge(_files.Describe(content, file.Route), file, attributes) is { } after)
            {
                return ContentReading.LeftOut(after);
            }

            return new ContentReading(ContentReadResult.Read, value, part is ContentPart.Whole || EndsAreWhole(file.Length));
        }
    }

    /// <summary>
    /// Why a file as described now must not be read, or have its reading kept, or null where it is
    /// still the file the search identified, on this device.
    /// </summary>
    /// <param name="attributes">The attributes described before the content was opened, which must not have changed since.</param>
    private static ContentReadResult? Judge(FileReading reading, DuplicateCandidate file, FileAttributes? attributes)
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
        var whole = part is ContentPart.Whole || EndsAreWhole(length);
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
    /// Opened sharing only reading, so no other program can write to the file while it is read, and
    /// for reading in order, which tells Windows to read ahead.
    /// </summary>
    private static SafeFileHandle OpenForContent(string extendedPath) =>
        File.OpenHandle(extendedPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
}
