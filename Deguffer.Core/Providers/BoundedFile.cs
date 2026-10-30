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
            using var stream = new FileStream(
                LongPath.Extended(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // One byte past the limit is read so that a file of exactly the limit is accepted and one
            // byte longer is not. The length Windows reports is not used, because the owning program
            // may be writing the file while it is read.
            var buffer = new byte[maximumBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            if (read > maximumBytes)
            {
                return null;
            }

            return buffer.AsMemory(0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone, locked or refused. None of those is the record, and every caller reads null as
            // "could not tell" rather than as "nothing here".
            return null;
        }
    }
}
