using System.Globalization;
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
/// <para>What is in a file is <see cref="ScanSummaryFormat"/>'s. This decides where the files are,
/// which of them are kept, and which may be removed.</para>
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
            using var stream = File.OpenRead(LongPath.Extended(kept.File));

            return ScanSummaryFormat.Read(stream, kept.Volume);
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
            {
                ScanSummaryFormat.Write(stream, summary);
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

    /// <summary>The header of the summary in <paramref name="file"/>, or null where it cannot be read.</summary>
    private static KeptSummary? Header(string file)
    {
        try
        {
            using var stream = File.OpenRead(LongPath.Extended(file));

            return ScanSummaryFormat.ReadHeader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

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
