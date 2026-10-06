using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// One stored summary, as far as its header describes it: enough to list it, chart it and remove it
/// without reading the folders in it.
/// </summary>
public sealed record KeptSummary(
    string Volume,
    string RootPath,
    DateTime TakenUtc,
    ScanStrategy Strategy,
    bool LowerBound,
    long TotalBytes,
    long FreeBytes)
{
    /// <summary>Where it is stored. Never shown and never logged: it names the user's profile.</summary>
    internal string File { get; init; } = string.Empty;

    /// <summary>What the volume said was in use when the scan finished.</summary>
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>
/// Reads and writes <see cref="ScanSummary"/> files under <c>%LOCALAPPDATA%\Deguffer\History</c>, a
/// folder per volume and a file per scan.
///
/// <para><b>A summary that cannot be read is passed over, never compared.</b> A torn write, a hand
/// edit and a format this build does not know all arrive as nothing to compare with, which costs the
/// user one comparison. Reading a damaged one could report a folder as removed, or as hundreds of
/// gigabytes of growth.</para>
///
/// <para><b>Gzipped JSON, with the header first.</b> JSON so that the file can be read and checked by
/// hand, and compressed because a summary is mostly repeated structure. The header comes before the
/// folders, so listing the kept summaries reads a few hundred bytes of each rather than all of
/// it.</para>
///
/// <para>Only files this store wrote are ever removed: a name it would have given, in a folder named
/// for a volume, under its own folder. Nothing here deletes a folder that still holds anything.</para>
/// </summary>
public sealed class ScanHistoryStore
{
    /// <summary>
    /// How many summaries are kept per volume. The oldest goes when a new one would pass it.
    ///
    /// <para>Twenty spans a few months at a scan a week, which is long enough to see a drive filling,
    /// and costs a few megabytes per volume at most.</para>
    /// </summary>
    public const int KeptPerVolume = 20;

    /// <summary>The format this build writes, and the only one it reads.</summary>
    private const int Format = 1;

    private const string Extension = ".json.gz";

    /// <summary>A UTC instant to the millisecond, which sorts as text in the order it sorts as time.</summary>
    private const string Stamp = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly string _root;

    public ScanHistoryStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _root = Path.Combine(environment.LocalAppData, "Deguffer", "History");
    }

    /// <summary>
    /// The summaries kept for <paramref name="volume"/>, oldest first, or none where none can be read.
    /// </summary>
    public IReadOnlyList<KeptSummary> List(string volume) =>
        FolderOf(volume) is { } folder ? [.. Headers(folder, volume).OrderBy(kept => kept.TakenUtc)] : [];

    /// <summary>Every summary kept for any volume, newest first.</summary>
    public IReadOnlyList<KeptSummary> List()
    {
        var all = new List<KeptSummary>();

        foreach (var folder in Folders())
        {
            all.AddRange(Headers(folder, volume: null));
        }

        return [.. all.OrderByDescending(kept => kept.TakenUtc)];
    }

    /// <summary>The whole of <paramref name="kept"/>, or null where it cannot be read.</summary>
    public ScanSummary? Read(KeptSummary kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        try
        {
            using var document = JsonDocument.Parse(Decompressed(kept.File));

            return Summary(document.RootElement, kept.Volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Gone since it was listed, refused, torn, or not gzip at all. Each is nothing to compare.
            return null;
        }
    }

    /// <summary>
    /// Store <paramref name="summary"/>, then remove the oldest of its volume's summaries past
    /// <see cref="KeptPerVolume"/>. Returns whether it was stored.
    /// </summary>
    public bool Save(ScanSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (FolderOf(summary.Volume) is not { } folder)
        {
            return false;
        }

        var file = Path.Combine(folder, summary.TakenUtc.ToUniversalTime().ToString(Stamp, CultureInfo.InvariantCulture) + Extension);
        var written = file + ".tmp";

        try
        {
            Directory.CreateDirectory(LongPath.Extended(folder));

            using (var stream = File.Create(LongPath.Extended(written)))
            using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
            using (var json = new Utf8JsonWriter(gzip))
            {
                Write(json, summary);
            }

            // Written beside and moved over, so an interrupted save leaves no torn summary behind
            // under a name the listing would read.
            File.Move(LongPath.Extended(written), LongPath.Extended(file), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(written);
            return false;
        }

        foreach (var old in Names(folder).OrderDescending(StringComparer.Ordinal).Skip(KeptPerVolume))
        {
            TryDelete(Path.Combine(folder, old));
        }

        return true;
    }

    /// <summary>Remove <paramref name="kept"/>. Returns whether it is gone.</summary>
    public bool Remove(KeptSummary kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        // Only a file this store would have written. A KeptSummary only ever comes from here, and this
        // is the check that keeps that true of anything handed back.
        if (!IsOurs(kept.File))
        {
            return false;
        }

        var removed = TryDelete(kept.File);
        TryRemoveFolder(Path.GetDirectoryName(kept.File)!);

        return removed;
    }

    /// <summary>Remove every kept summary of every volume. Returns whether all of them are gone.</summary>
    public bool RemoveAll()
    {
        var all = true;

        foreach (var folder in Folders())
        {
            foreach (var name in Names(folder))
            {
                all &= TryDelete(Path.Combine(folder, name));
            }

            TryRemoveFolder(folder);
        }

        return all;
    }

    /// <summary>
    /// The folder a volume's summaries go in, named for the GUID in its <c>\\?\Volume{GUID}\</c> name,
    /// or null where the name holds none. Parsed rather than copied, so nothing but a GUID can ever
    /// reach the path.
    /// </summary>
    private string? FolderOf(string volume)
    {
        var open = volume.IndexOf('{', StringComparison.Ordinal);
        var close = volume.IndexOf('}', StringComparison.Ordinal);

        return open >= 0 && close > open && Guid.TryParse(volume.AsSpan(open, close - open + 1), out var id)
            ? Path.Combine(_root, id.ToString("B"))
            : null;
    }

    /// <summary>Every folder under the root named as <see cref="FolderOf"/> names one.</summary>
    private IEnumerable<string> Folders()
    {
        IEnumerable<string> folders;

        try
        {
            folders = [.. Directory.EnumerateDirectories(LongPath.Extended(_root))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No history yet, or a folder that cannot be listed: nothing kept that can be read.
            return [];
        }

        return folders
            .Select(LongPath.Display)
            .Where(folder => Guid.TryParseExact(Path.GetFileName(folder), "B", out _));
    }

    /// <summary>The names of the summaries in <paramref name="folder"/>, by the name this store gives one.</summary>
    private static IEnumerable<string> Names(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(LongPath.Extended(folder), "*" + Extension)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(IsStampName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsStampName(string name) =>
        name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && DateTime.TryParseExact(
            name[..^Extension.Length], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _);

    private bool IsOurs(string file) =>
        Path.GetDirectoryName(file) is { } folder
        && string.Equals(Path.GetDirectoryName(folder), _root, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(Path.GetFileName(folder), "B", out _)
        && IsStampName(Path.GetFileName(file));

    /// <summary>The header of every readable summary in <paramref name="folder"/>.</summary>
    /// <param name="volume">The volume the folder is for, or null to take it from each header.</param>
    private static IEnumerable<KeptSummary> Headers(string folder, string? volume)
    {
        foreach (var name in Names(folder))
        {
            var file = Path.Combine(folder, name);

            if (Header(file) is { } kept && (volume is null || kept.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase)))
            {
                yield return kept with { File = file };
            }
        }
    }

    /// <summary>
    /// The header of the summary in <paramref name="file"/>, read until the folders begin and no
    /// further, or null where it cannot be read.
    /// </summary>
    private static KeptSummary? Header(string file)
    {
        try
        {
            using var stream = File.OpenRead(LongPath.Extended(file));
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
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
                || !entry[0].TryGetInt32(out var parent)
                || entry[1].ValueKind != JsonValueKind.String || entry[1].GetString() is not { Length: > 0 } name
                || !entry[2].TryGetInt64(out var bytes) || bytes < 0)
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

    private static void Write(Utf8JsonWriter json, ScanSummary summary)
    {
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

        // Last, so that a header can be read without it. See Header.
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

    private static byte[] Decompressed(string file)
    {
        using var stream = File.OpenRead(LongPath.Extended(file));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var bytes = new MemoryStream();

        gzip.CopyTo(bytes);

        return bytes.ToArray();
    }

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

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(LongPath.Extended(file));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove a volume's folder once nothing is left in it. Not recursive, so a folder still holding
    /// anything, this store's or not, stays.
    /// </summary>
    private static void TryRemoveFolder(string folder)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(LongPath.Extended(folder)).Any())
            {
                Directory.Delete(LongPath.Extended(folder), recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held, or refused. An empty folder left behind costs nothing.
        }
    }
}
