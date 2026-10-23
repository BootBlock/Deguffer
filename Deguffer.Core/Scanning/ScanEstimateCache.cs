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
///
/// <para><b>Written once a pass, not once a measurement.</b> A pass measures its targets one after
/// another, and on a first run nearly every figure is new, so writing the file for each would write
/// it as often as there are targets and serialise every entry each time, with antivirus scanning
/// each write. A changed figure only marks the cache changed while a <see cref="Hold"/> stands, and
/// the last hold to end writes it once. A figure set with no hold standing is written straight
/// away, because nothing will end to write it. The file is serialised from a copy taken under the
/// lock and written outside it, so a measurement on another thread never waits on the disk for the
/// lock.</para>
///
/// <para>One instance for each file. Two over the same file would each write back only what they
/// had loaded and set, and the later write would take the other's figures away, so every scanner of
/// this machine shares <see cref="For"/>'s.</para>
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
    private readonly Action<string, string> _store;

    /// <summary>Whether a figure has changed since the file was last written. Under <see cref="_gate"/>.</summary>
    private bool _changed;

    /// <summary>The holds standing. Under <see cref="_gate"/>.</summary>
    private int _holds;

    /// <summary>
    /// Counts each copy taken for writing, under <see cref="_gate"/>, so a write that reaches the disk
    /// after a later copy's write leaves the later one in place.
    /// </summary>
    private long _copied;

    /// <summary>The newest copy written. Under <see cref="_writing"/>.</summary>
    private long _written;

    /// <summary>Taken for the write alone, so one write cannot interleave with another.</summary>
    private readonly Lock _writing = new();

    public ScanEstimateCache(IUserEnvironment environment)
        : this(environment, StoreFile.Replace)
    {
    }

    /// <param name="store">Writes the file's whole text: the seam a test counts writes through.</param>
    internal ScanEstimateCache(IUserEnvironment environment, Action<string, string> store)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _file = Path.Combine(environment.LocalAppData, "Deguffer", "scan-estimates.json");
        _store = store;
        Load();
    }

    /// <summary>
    /// The cache of the machine <paramref name="environment"/> describes. One shared instance for this
    /// machine, which every <see cref="UserEnvironment"/> describes, so there is one for its file, as
    /// <see cref="DirectoryScanner.For"/> shares one scanner. Any other environment is a machine of its
    /// caller's own and gets a cache of its own.
    /// </summary>
    public static ScanEstimateCache For(IUserEnvironment environment) =>
        environment is UserEnvironment ? Machine : new ScanEstimateCache(environment);

    private static ScanEstimateCache Machine { get; } = new(UserEnvironment.Current);

    /// <param name="Entries">
    /// Absent from a file written before the count existed, which then reads as nothing counted. The
    /// figure is shown only until the real measurement arrives, so that costs one briefly low number.
    /// </param>
    private sealed record Entry(long Allocated, long Logical, bool IsApproximate, DateTimeOffset MeasuredAt, long Entries = 0);

    /// <summary>The last known size of <paramref name="path"/>, if it is recent enough to show.</summary>
    /// <param name="keepsRoot">
    /// Whether the figure is of the folder's contents alone, with the folder itself kept standing. See
    /// <see cref="ScanBounds.KeepRoot"/>.
    /// </param>
    public ScanSize? TryGet(string path, bool keepsRoot = false)
    {
        var key = Key(path, keepsRoot);

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

            return new ScanSize(entry.Allocated, entry.Logical, entry.IsApproximate, entry.Entries);
        }
    }

    /// <param name="keepsRoot">As <see cref="TryGet"/> takes it.</param>
    public void Set(string path, ScanSize size, bool keepsRoot = false)
    {
        var key = Key(path, keepsRoot);

        lock (_gate)
        {
            var replacement = new Entry(
                size.Allocated, size.Logical, size.IsApproximate, DateTimeOffset.UtcNow, size.Entries);

            // Re-measuring an unchanged cache is the common case, and rewriting the whole file to
            // record the same numbers with a newer timestamp is pure I/O on the scanning path.
            var unchanged = _entries.TryGetValue(key, out var existing)
                && existing.Allocated == size.Allocated
                && existing.Logical == size.Logical
                && existing.IsApproximate == size.IsApproximate
                && existing.Entries == size.Entries
                && !IsStale(existing);

            _entries[key] = replacement;

            if (unchanged)
            {
                return;
            }

            _changed = true;

            if (_holds > 0)
            {
                return;
            }
        }

        Save();
    }

    /// <summary>
    /// Keep changed figures in memory until the hold ends, and write them once when the last hold
    /// standing ends. Taken for each planning pass through <see cref="DirectoryScanner.Hold"/>, and
    /// for each measurement call that remembers several figures, so it writes once however many.
    /// </summary>
    public PassHold Hold()
    {
        lock (_gate)
        {
            _holds++;
        }

        return new PassHold(Release);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holds > 0)
            {
                return;
            }
        }

        Save();
    }

    /// <summary>
    /// Write the file now where any figure changed since it was last written, holds standing or not.
    /// For the app's exit and its relaunch, when a pass under way will not end in this process.
    /// </summary>
    public void Save()
    {
        KeyValuePair<string, Entry>[] copy;
        long copied;

        lock (_gate)
        {
            if (!_changed)
            {
                return;
            }

            _changed = false;
            copy = [.. _entries];
            copied = ++_copied;
        }

        lock (_writing)
        {
            if (copied < _written)
            {
                return;
            }

            try
            {
                _store(_file, JsonSerializer.Serialize(new Dictionary<string, Entry>(copy, StringComparer.OrdinalIgnoreCase), SerializerOptions));
                _written = copied;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept as changed, so the next pass or the exit tries again. Losing the file costs a
                // slower first paint next time and nothing else.
                lock (_gate)
                {
                    _changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Paths reach here in whatever spelling the provider resolved — with or without a trailing
    /// separator, and with or without §6.3's extended-length prefix. Keying on the raw string would
    /// make two spellings of one directory miss each other, and the cache would quietly stop
    /// working with no symptom beyond a blank window on reopen.
    /// </summary>
    private static string Key(string path) => LongPath.Entry(path) ?? path;

    /// <summary>
    /// The key for a figure of the path whole, or of its contents alone. The two differ by the folder's
    /// own entry wherever nothing inside it stays, so one shown in place of the other would open on a
    /// count that is one out. The marker cannot be part of a Windows path, so it names nothing on disk.
    /// </summary>
    private static string Key(string path, bool keepsRoot) => keepsRoot ? Key(path) + ContentsMarker : Key(path);

    private const string ContentsMarker = "|contents";

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
                    var contents = path.EndsWith(ContentsMarker, StringComparison.Ordinal);

                    _entries[Key(contents ? path[..^ContentsMarker.Length] : path, contents)] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache that cannot be read is a cache miss, not a failure. The file is replaced whole,
            // so a stopped write cannot tear it, but a hand edit or a disk fault still can.
        }
    }
}
