using System.Runtime.InteropServices;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A small file another program wrote, read no further than a stated size.
///
/// <para>Bounded because the files read this way sit in folders Deguffer does not own, and a file
/// that has grown past anything its program writes is not the record it is named for. Reading it
/// whole anyway would put an arbitrary amount of somebody else's data in memory to answer a question
/// about one field.</para>
///
/// <para><b>Some of these files hold a secret beside the field that is wanted.</b> An editor's
/// handshake lock carries an authentication token, and Spotify's settings carry a saved sign-in. A
/// caller takes the fields it names and nothing else, and nothing read here is ever logged, shown or
/// quoted.</para>
/// </summary>
internal static class BoundedFile
{
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// UTF-8 that refuses invalid bytes, for a caller decoding what it read here as text: a file that
    /// is not text is not the record it is named for, and a lenient decoder would hide that.
    /// </summary>
    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The file's content with any UTF-8 byte order mark removed, or null where the file is missing,
    /// larger than <paramref name="maximumBytes"/>, or unreadable.
    /// </summary>
    public static ReadOnlyMemory<byte>? Read(string path, int maximumBytes)
    {
        // Statements, not one conditional expression: there, null takes the expression's type through
        // byte[]'s conversion to ReadOnlyMemory<byte>, and an unreadable file comes back empty.
        if (ReadVerbatim(path, maximumBytes) is not { } content)
        {
            return null;
        }

        return content.Span.StartsWith(ByteOrderMark) ? content[ByteOrderMark.Length..] : content;
    }

    /// <summary>
    /// The file's content exactly as it is on disk, byte order mark included, or null where the file
    /// is missing, larger than <paramref name="maximumBytes"/>, or unreadable.
    ///
    /// <para>For a format that fixes its opening bytes. The cache directory tag's header must be the
    /// first 43 bytes of the file, so a tag opening with a byte order mark is not one, and removing
    /// the mark before the comparison would accept it.</para>
    /// </summary>
    public static ReadOnlyMemory<byte>? ReadVerbatim(string path, int maximumBytes)
    {
        try
        {
            // Unbuffered, because every read below goes straight into the buffer it returns, and a
            // FileStream's own buffer would be a second allocation for each file.
            using var stream = new FileStream(
                LongPath.Extended(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0);

            return ReadVerbatim(stream, maximumBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone, locked or refused. None of those is the record, and every caller reads null as
            // "could not tell" rather than as "nothing here".
            return null;
        }
    }

    /// <summary>
    /// What remains of <paramref name="stream"/>, or null where that is more than
    /// <paramref name="maximumBytes"/>. Separate from the path so that a test can hand it a stream
    /// whose reported length is not what it holds.
    /// </summary>
    internal static ReadOnlyMemory<byte>? ReadVerbatim(Stream stream, int maximumBytes)
    {
        // One byte past the limit is read so that a file of exactly the limit is accepted and one
        // byte longer is not.
        var limit = maximumBytes + 1;

        // The length Windows reports sizes the first buffer, but never bounds the read: the owning
        // program may be writing the file while it is read, so the loop reads to the end or to the
        // limit, whichever comes first, and what it read is what is judged. One byte over the
        // reported length lets the read that finds the end land without growing the buffer. Sizing
        // by the limit instead would zero up to 16 MB, on the large object heap, for a file of a few
        // kilobytes.
        var reported = stream.CanSeek ? Math.Max(stream.Length - stream.Position, 0) : 0;
        var buffer = new byte[(int)Math.Min(reported + 1, limit)];
        var read = 0;

        while (true)
        {
            if (read == buffer.Length)
            {
                if (read == limit)
                {
                    break;
                }

                // Not pooled: some of these files hold a secret, which a rented array would carry
                // to its next renter.
                Array.Resize(ref buffer, (int)Math.Min(2L * buffer.Length, limit));
            }

            var count = stream.Read(buffer, read, buffer.Length - read);

            if (count == 0)
            {
                break;
            }

            read += count;
        }

        // Statements, for the reason Read gives: a conditional expression would return an empty
        // buffer for a file past the limit.
        if (read > maximumBytes)
        {
            return null;
        }

        return buffer.AsMemory(0, read);
    }

    /// <summary>
    /// A read-only stream over <paramref name="content"/>, for a parser that reads from a stream,
    /// without copying what was read here a second time.
    /// </summary>
    public static MemoryStream AsStream(ReadOnlyMemory<byte> content) =>
        MemoryMarshal.TryGetArray(content, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);
}
