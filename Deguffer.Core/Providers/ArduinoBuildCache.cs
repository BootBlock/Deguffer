using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One compiled core or sketch in the Arduino build cache.</summary>
/// <param name="Path">The entry's folder, in display form.</param>
/// <param name="Kind">The folder of the cache it is in: <c>cores</c> or <c>sketches</c>.</param>
/// <param name="LastUsed">When the CLI last used it, from its <c>.last-used</c> marker.</param>
internal sealed record ArduinoBuildEntry(string Path, string Kind, DateTime LastUsed);

/// <summary>What one Arduino build cache folder holds.</summary>
/// <param name="Entries">Each entry the CLI marked as its own.</param>
/// <param name="Unrecognised">Everything else in the cache folder or in its two parts, with why it stays.</param>
/// <param name="Unlisted">The folders Windows would not list, so nothing here says what they hold.</param>
internal sealed record ArduinoBuildCacheScan(
    IReadOnlyList<ArduinoBuildEntry> Entries,
    IReadOnlyList<(string Path, string Reason)> Unrecognised,
    IReadOnlyList<string> Unlisted);

/// <summary>
/// The Arduino CLI's build cache, read by the marker the CLI writes into each entry.
///
/// <para><b>Recognised by the CLI's own mark.</b> Since arduino-cli 1.0.4 the cache is
/// <c>build_cache.path</c>, by default <c>%LOCALAPPDATA%\arduino</c>, and holds two folders:
/// <c>cores</c>, one archived core per board and options, and <c>sketches</c>, one build folder per
/// sketch path. The CLI writes an empty <c>.last-used</c> file into every entry each time it uses it
/// (<c>internal/buildcache/build_cache.go</c>), so an entry without one is not the CLI's, and stays
/// (§5.2).</para>
///
/// <para><b>Its own purge does not reach this folder.</b> <c>maybePurgeBuildCache</c> in
/// <c>commands/service_compile.go</c> purges <c>%TEMP%\arduino\cores</c> and
/// <c>%TEMP%\arduino\sketches</c>, which is where the cache was before 1.0.4. So the folder the CLI now
/// uses is never trimmed by the CLI, and nothing but this row removes an entry from it.</para>
/// </summary>
internal static class ArduinoBuildCache
{
    /// <summary>The CLI's marker, written into every entry it uses.</summary>
    public const string LastUsedName = ".last-used";

    /// <summary>The two folders of the cache, each a set of entries the CLI keys by a hash.</summary>
    public static readonly IReadOnlyList<string> Parts = ["cores", "sketches"];

    public static ArduinoBuildCacheScan Read(string cache)
    {
        var entries = new List<ArduinoBuildEntry>();
        var unrecognised = new List<(string Path, string Reason)>();
        var unlisted = new List<string>();

        if (FolderEntries.Of(cache) is not { } top)
        {
            return new ArduinoBuildCacheScan([], [], [cache]);
        }

        foreach (var entry in top)
        {
            if (entry is DirectoryInfo part
                && !part.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && Parts.Contains(part.Name, StringComparer.OrdinalIgnoreCase))
            {
                ReadPart(part, entries, unrecognised, unlisted);
            }
            else
            {
                unrecognised.Add((LongPath.Display(entry.FullName), "Not part of the Arduino build cache, so Deguffer leaves it alone."));
            }
        }

        return new ArduinoBuildCacheScan(entries, unrecognised, unlisted);
    }

    private static void ReadPart(
        DirectoryInfo part,
        List<ArduinoBuildEntry> entries,
        List<(string Path, string Reason)> unrecognised,
        List<string> unlisted)
    {
        var folder = LongPath.Display(part.FullName);

        if (FolderEntries.Of(folder) is not { } children)
        {
            unlisted.Add(folder);
            return;
        }

        foreach (var child in children)
        {
            var path = LongPath.Display(child.FullName);

            if (child is not DirectoryInfo directory || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                unrecognised.Add((path, "Not an entry the Arduino CLI made, so Deguffer leaves it alone."));
                continue;
            }

            var marker = Path.Combine(path, LastUsedName);

            if (LongPath.ProbeFile(marker) is not PathPresence.Present)
            {
                unrecognised.Add((path, $"Has no {LastUsedName} marker, so it is not an entry the Arduino CLI made."));
                continue;
            }

            entries.Add(new ArduinoBuildEntry(path, part.Name, new FileInfo(LongPath.Extended(marker)).LastWriteTimeUtc));
        }
    }
}
