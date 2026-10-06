using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// What a stored <see cref="ScanSummary"/> file holds, and the rules a file has to keep to be read.
///
/// <para><b>Gzipped JSON, with the header first.</b> JSON so that a file can be read and checked by
/// hand, and compressed because a summary is mostly repeated structure. The header comes before the
/// folders, so listing the kept summaries reads a few hundred bytes of each rather than all of
/// it.</para>
///
/// <para><b>Anything that breaks a rule is not a summary.</b> Each read answers null rather than a
/// summary with the broken part left out, because a comparison against part of a record reports what
/// is missing from it as removed. Damage the stream itself reports, a torn gzip or JSON that does not
/// parse, arrives as the exception it is, for <see cref="ScanHistoryStore"/> to pass over.</para>
///
/// <para>Apart from the store because it is a different job (G1): the store decides where the files
/// are and which are kept, and this decides what is in one.</para>
/// </summary>
internal static class ScanSummaryFormat
{
    /// <summary>The format this build writes, and the only one it reads.</summary>
    private const int Format = 1;

    /// <summary>Write <paramref name="summary"/> to <paramref name="destination"/>, compressed.</summary>
    internal static void Write(Stream destination, ScanSummary summary)
    {
        using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        using var json = new Utf8JsonWriter(gzip);

        json.WriteStartObject();
        json.WriteNumber("Format", Format);
        json.WriteString("Volume", summary.Volume);
        json.WriteString("RootPath", summary.RootPath);
        json.WriteString("TakenUtc", summary.TakenUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("Strategy", summary.Strategy.ToString());
        json.WriteBoolean("LowerBound", summary.LowerBound);
        json.WriteNumber("TotalBytes", summary.TotalBytes);
        json.WriteNumber("FreeBytes", summary.FreeBytes);
        json.WriteNumber("UnrecordedAtMost", summary.UnrecordedAtMost);

        // Last, so that a header can be read without it. See ReadHeader.
        json.WriteStartArray("Folders");

        foreach (var folder in summary.Folders)
        {
            json.WriteStartArray();
            json.WriteNumberValue(folder.Parent);
            json.WriteStringValue(folder.Name);
            json.WriteNumberValue(folder.Bytes);
            json.WriteEndArray();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// The whole summary in <paramref name="source"/>, of <paramref name="volume"/>, or null where any
    /// part of it is not what <see cref="Write"/> writes.
    /// </summary>
    internal static ScanSummary? Read(Stream source, string volume)
    {
        using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var bytes = new MemoryStream();

        gzip.CopyTo(bytes);

        using var document = JsonDocument.Parse(bytes.ToArray());

        return Summary(document.RootElement, volume);
    }

    /// <summary>
    /// The header of the summary in <paramref name="source"/>, read until the folders begin and no
    /// further, or null where it is not a summary this build can read.
    /// </summary>
    internal static KeptSummary? ReadHeader(Stream source)
    {
        using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);

        var buffer = new byte[4096];
        var filled = 0;

        // More of the stream each time round, read again from the start, until the header is
        // whole. A header is a few hundred bytes, so this is nearly always one pass; a root path
        // can be long, and the buffer grows for it.
        while (true)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = gzip.Read(buffer, filled, buffer.Length - filled);
            filled += read;

            if (HeaderIn(buffer.AsSpan(0, filled), isFinal: read == 0) is { } header)
            {
                return header.Complete ? header.Kept : null;
            }
        }
    }

    /// <summary>
    /// The header in <paramref name="bytes"/>, or null where more of the file is needed to finish it.
    /// <c>Complete</c> is false where the file is not a summary this build can read.
    /// </summary>
    private static (bool Complete, KeptSummary? Kept)? HeaderIn(ReadOnlySpan<byte> bytes, bool isFinal)
    {
        var reader = new Utf8JsonReader(bytes, isFinal, default);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!reader.Read())
        {
            return isFinal ? (false, null) : null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return (false, null);
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (false, null);
            }

            var name = reader.GetString()!;

            if (name == "Folders")
            {
                return (true, Kept(fields));
            }

            if (!JsonElement.TryParseValue(ref reader, out var value))
            {
                return isFinal ? (false, null) : null;
            }

            fields[name] = value.Value;
        }

        return isFinal ? (false, null) : null;
    }

    private static KeptSummary? Kept(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("Format", out var format) || format.ValueKind != JsonValueKind.Number
            || !format.TryGetInt32(out var version) || version != Format
            || Text(fields, "Volume") is not { } volume
            || Text(fields, "RootPath") is not { } rootPath
            || Text(fields, "TakenUtc") is not { } taken
            || !DateTime.TryParseExact(taken, "O", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var takenUtc)
            || !Enum.TryParse<ScanStrategy>(Text(fields, "Strategy"), out var strategy) || !Enum.IsDefined(strategy)
            || !fields.TryGetValue("LowerBound", out var lowerBound) || lowerBound.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || Bytes(fields, "TotalBytes") is not { } total
            || Bytes(fields, "FreeBytes") is not { } free || free > total
            || Bytes(fields, "UnrecordedAtMost") is null)
        {
            return null;
        }

        return new KeptSummary(volume, rootPath, takenUtc, strategy, lowerBound.GetBoolean(), total, free);
    }

    /// <summary>
    /// The summary in <paramref name="root"/>, of <paramref name="volume"/>, or null where any part of
    /// it is not what <see cref="Write"/> writes.
    /// </summary>
    private static ScanSummary? Summary(JsonElement root, string volume)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        if (Kept(fields) is not { } kept || !kept.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase)
            || !fields.TryGetValue("Folders", out var listed) || Folders(listed) is not { } folders)
        {
            return null;
        }

        return new ScanSummary(
            kept.Volume,
            kept.RootPath,
            kept.TakenUtc,
            kept.Strategy,
            kept.LowerBound,
            kept.TotalBytes,
            kept.FreeBytes,
            Bytes(fields, "UnrecordedAtMost")!.Value,
            folders);
    }

    /// <summary>
    /// The folders, or null where any one breaks the rules a comparison relies on: the root first and
    /// alone at -1, every other parent earlier than its child, a plain name, and a size that is not
    /// negative.
    /// </summary>
    private static List<SummaryFolder>? Folders(JsonElement listed)
    {
        if (listed.ValueKind != JsonValueKind.Array || listed.GetArrayLength() == 0)
        {
            return null;
        }

        var folders = new List<SummaryFolder>(listed.GetArrayLength());

        foreach (var entry in listed.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 3
                || entry[0].ValueKind != JsonValueKind.Number || !entry[0].TryGetInt32(out var parent)
                || entry[1].ValueKind != JsonValueKind.String || entry[1].GetString() is not { Length: > 0 } name
                || entry[2].ValueKind != JsonValueKind.Number || !entry[2].TryGetInt64(out var bytes) || bytes < 0)
            {
                return null;
            }

            var isRoot = folders.Count == 0;

            if (isRoot ? parent != -1 : parent < 0 || parent >= folders.Count || !IsPlainName(name))
            {
                return null;
            }

            folders.Add(new SummaryFolder(parent, name, bytes));
        }

        return folders;
    }

    private static bool IsPlainName(string name) =>
        name is not ("." or "..") && name.IndexOfAny(['\\', '/', ':']) < 0;

    private static string? Text(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static long? Bytes(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var bytes) && bytes >= 0
            ? bytes
            : null;
}
