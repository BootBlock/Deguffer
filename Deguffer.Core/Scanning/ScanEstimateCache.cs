using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Remembers the last measured size of each path across runs, so re-opening the tool shows numbers
/// immediately instead of an empty window (§5.5).
///
/// **These values are for first paint only, and are never the figure Deguffer acts on.** Every
/// measurement re-scans and returns the fresh number; the cache only fills the gap while that
/// happens. The distinction is load-bearing rather than pedantic: <see cref="Execution.PlanExecutor"/>
/// reports what a tool's own eviction command reclaimed by subtracting the after-size from the
/// plan-time estimate, so a stale value used as authoritative would not merely look wrong — it
/// would overstate reclaimed space in the one number §7 says the user came for.
///
/// Being advisory is also what makes a simple time-based expiry sufficient. A tree's size can
/// change without anything in it changing its own timestamp, so no cheap filesystem signal is a
/// sound validator; the USN journal is the sound one, and it needs the same elevation the MFT does,
/// which would leave the fallback path with no invalidation at all.
/// </summary>
public sealed class ScanEstimateCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// Long, because a stale entry costs a briefly-wrong number on screen and nothing else, while
    /// a short one costs the instant re-open this exists to provide.
    /// </summary>
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly string _file;

    public ScanEstimateCache(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _file = Path.Combine(environment.LocalAppData, "Deguffer", "scan-estimates.json");
        Load();
    }

    private sealed record Entry(long Allocated, long Logical, bool IsApproximate, DateTimeOffset MeasuredAt);

    /// <summary>The last known size of <paramref name="path"/>, if it is recent enough to show.</summary>
    public ScanSize? TryGet(string path)
    {
        var key = Key(path);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (IsStale(entry))
            {
                // Drop it here rather than merely declining to use it. Left in place, an expired
                // entry is reloaded and rewritten on every run for the life of the machine.
                _entries.Remove(key);
                return null;
            }

            return new ScanSize(entry.Allocated, entry.Logical, entry.IsApproximate);
        }
    }

    public void Set(string path, ScanSize size)
    {
        var key = Key(path);

        lock (_gate)
        {
            var replacement = new Entry(size.Allocated, size.Logical, size.IsApproximate, DateTimeOffset.UtcNow);

            // Re-measuring an unchanged cache is the common case, and rewriting the whole file to
            // record the same numbers with a newer timestamp is pure I/O on the scanning path.
            var unchanged = _entries.TryGetValue(key, out var existing)
                && existing.Allocated == size.Allocated
                && existing.Logical == size.Logical
                && existing.IsApproximate == size.IsApproximate
                && !IsStale(existing);

            _entries[key] = replacement;

            if (!unchanged)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Paths reach here in whatever spelling the provider resolved — with or without a trailing
    /// separator, and with or without §6.3's extended-length prefix. Keying on the raw string would
    /// make two spellings of one directory miss each other, and the cache would quietly stop
    /// working with no symptom beyond a blank window on reopen.
    /// </summary>
    private static string Key(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(LongPath.Display(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsStale(Entry entry) => DateTimeOffset.UtcNow - entry.MeasuredAt > MaximumAge;

    private void Load()
    {
        try
        {
            if (!LongPath.FileExists(_file))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(LongPath.Extended(_file)), SerializerOptions);

            if (loaded is null)
            {
                return;
            }

            foreach (var (path, entry) in loaded)
            {
                // Expired entries are dropped on the way in, so a long-lived cache cannot
                // accumulate every path the machine has ever had.
                if (!IsStale(entry))
                {
                    _entries[Key(path)] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache that cannot be read is a cache miss, not a failure. Corrupt content is
            // expected here specifically because the file is written on every measurement and the
            // process can be killed mid-write.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_file);
            if (directory is not null)
            {
                Directory.CreateDirectory(LongPath.Extended(directory));
            }

            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(_entries, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the cache costs a slower first paint next time and nothing else.
        }
    }
}
