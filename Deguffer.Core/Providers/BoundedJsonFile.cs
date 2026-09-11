using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A small JSON record another program wrote, read no further than a stated size.
///
/// <para>Bounded because the files read this way sit in folders Deguffer does not own, and a file
/// that has grown past anything its program writes is not the record it is named for. Reading it
/// whole anyway would put an arbitrary amount of somebody else's data in memory to answer a question
/// about one field.</para>
///
/// <para><b>Some of these records hold a secret beside the field that is wanted.</b> An editor's
/// handshake lock carries an authentication token, and a messaging key carries a peer token. A caller
/// takes the fields it names and nothing else, and nothing read here is ever logged, shown or
/// quoted.</para>
/// </summary>
internal static class BoundedJsonFile
{
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The record, or null where the file is missing, larger than <paramref name="maximumBytes"/>,
    /// unreadable, not JSON, or not a JSON object. The caller disposes what it is given.
    /// </summary>
    public static JsonDocument? Read(string path, int maximumBytes)
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

            var content = buffer.AsMemory(0, read);

            if (content.Span.StartsWith(ByteOrderMark))
            {
                content = content[ByteOrderMark.Length..];
            }

            var document = JsonDocument.Parse(content);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Gone, locked, refused, or caught halfway through being written. None of those is the
            // record, and every caller reads null as "could not tell" rather than as "nothing here".
            return null;
        }
    }

    /// <summary>A string property of the record, or null where it is absent or is not a string.</summary>
    public static string? StringProperty(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
