using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Sync roots and placeholders held in memory, for the rules that decide which local copies go.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real <see cref="CloudFiles"/> walks and unpins
/// the cloud accounts of whoever runs the suite. <c>CloudFilesTests</c> covers the real calls
/// against a scratch sync root of its own.</para>
///
/// <para><b>It behaves as the API was observed to, not as the rules would like.</b>
/// <see cref="Release"/> unpins whatever the caller's predicate accepts, because <c>CfSetPinState</c>
/// refuses nothing: every rule under test is then Deguffer's own.</para>
///
/// <para><b>Safe to call from several threads at once</b>, as a release and its proof call it. The
/// hooks run outside the fake's lock, so a hook may change the fake, and a test may hold one call
/// there while others go on.</para>
/// </summary>
public sealed class FakeCloudFiles : ICloudFiles
{
    private readonly Lock _gate = new();
    private readonly List<SyncRoot> _roots = [];
    private readonly Dictionary<string, SyncProviderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _redirected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rootLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _asked = [];
    private readonly List<string> _unpinned = [];

    /// <summary>Set to make <see cref="SyncRoots"/> answer as Windows does when it will not list them.</summary>
    public bool RefusesToListRoots { get; set; }

    /// <summary>Well before any guard window a test sets, so a file is old unless it says otherwise.</summary>
    public static readonly long LongAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>
    /// Every path a release was asked about, whatever came of it, in the order the requests began.
    /// Requests made at once begin in no fixed order.
    /// </summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>The paths whose pin a release changed, in the order it changed them.</summary>
    public IReadOnlyList<string> Unpinned
    {
        get
        {
            lock (_gate)
            {
                return [.. _unpinned];
            }
        }
    }

    /// <summary>
    /// Run as each release begins, before the file is looked at, so a test can change the disk between
    /// the preview and the moment a file is judged again, or hold a release while others begin.
    /// </summary>
    public Action<string>? BeforeRelease { get; set; }

    /// <summary>Run as each reading begins, before the path is looked at, so a test can hold one while others begin.</summary>
    public Action<string>? BeforeRead { get; set; }

    public FakeCloudFiles Root(string id, string path, string displayName, SyncProviderState state = SyncProviderState.Running)
    {
        lock (_gate)
        {
            _roots.Add(new SyncRoot(id, path, displayName));
            _states[path] = state;
        }

        return Put(new Entry(path, IsDirectory: true, EntryKind.Placeholder, Placeholder(0)));
    }

    public FakeCloudFiles Folder(string path, PinState pin = PinState.Unspecified) =>
        Put(new Entry(path, IsDirectory: true, EntryKind.Placeholder, Placeholder(0) with { Pin = pin }));

    /// <summary>A folder that is not a placeholder, which a sync app may still keep placeholders under.</summary>
    public FakeCloudFiles PlainFolder(string path) => Put(new Entry(path, IsDirectory: true, EntryKind.Plain, null));

    /// <summary>A junction or symbolic link to a folder, which is never entered.</summary>
    public FakeCloudFiles Link(string path) => Put(new Entry(path, IsDirectory: true, EntryKind.OtherLink, null));

    public FakeCloudFiles File(
        string path,
        long onDisk,
        long modified = 0,
        bool inSync = true,
        PinState pin = PinState.Unspecified,
        long? newest = null) =>
        Put(new Entry(
            path,
            IsDirectory: false,
            EntryKind.Placeholder,
            new Placeholder(onDisk, modified, inSync, pin, newest ?? LongAgo)));

    /// <summary>An ordinary file inside a sync root, which no rule may touch.</summary>
    public FakeCloudFiles PlainFile(string path) => Put(new Entry(path, IsDirectory: false, EntryKind.Plain, null));

    /// <summary>An entry Windows lists and will not describe.</summary>
    public FakeCloudFiles Refuse(string path)
    {
        lock (_gate)
        {
            _entries[path] = _entries[path] with { Refused = true };
        }

        return this;
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => LongPath.Contains(path, key)).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    public void Change(string path, Func<Placeholder, Placeholder> change)
    {
        lock (_gate)
        {
            _entries[path] = _entries[path] with { Placeholder = change(_entries[path].Placeholder!) };
        }
    }

    public void Stop(string root)
    {
        lock (_gate)
        {
            _states[root] = SyncProviderState.NotRunning;
        }
    }

    /// <summary>
    /// Make a file open somewhere else, as a folder above it turned into a junction would: the handle
    /// then resolves to <paramref name="actually"/> rather than to the file named.
    /// </summary>
    public void Redirect(string path, string actually)
    {
        lock (_gate)
        {
            _redirected[path] = actually;
        }
    }

    /// <summary>
    /// Make a root really live at <paramref name="location"/>, as one the user reaches through a link of
    /// their own does: every file under it then resolves under that location.
    /// </summary>
    public void Locate(string root, string location)
    {
        lock (_gate)
        {
            _rootLocations[root] = location;
        }
    }

    public Placeholder? PlaceholderAt(string path)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(path)?.Placeholder;
        }
    }

    public IReadOnlyList<SyncRoot>? SyncRoots()
    {
        lock (_gate)
        {
            return RefusesToListRoots ? null : [.. _roots];
        }
    }

    /// <summary>
    /// Where a root really is, in display form rather than the device form Windows gives: this fake's
    /// locations are compared only with each other.
    /// </summary>
    public VolumeDevicePath? Resolve(string path)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(path) ? new VolumeDevicePath(_rootLocations.GetValueOrDefault(path, path)) : null;
        }
    }

    public SyncProviderState ProviderState(string syncRoot)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(syncRoot, SyncProviderState.Unknown);
        }
    }

    public IEnumerable<CloudEntry> List(string directory, CancellationToken ct)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => string.Equals(Path.GetDirectoryName(entry.Path), directory, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new CloudEntry(
                    entry.Path,
                    entry.IsDirectory,
                    IsPlaceholder: entry.Kind == EntryKind.Placeholder,
                    IsOtherLink: entry.Kind == EntryKind.OtherLink))
                .ToList();
        }
    }

    public PlaceholderReading Read(string path)
    {
        BeforeRead?.Invoke(path);

        lock (_gate)
        {
            return _entries.GetValueOrDefault(path) switch
            {
                null => PlaceholderReading.Absent,
                { Refused: true } => PlaceholderReading.Refused,
                var entry => new PlaceholderReading(PathPresence.Present, entry.Placeholder),
            };
        }
    }

    public ReleaseAnswer Release(string path, VolumeDevicePath resolvedPath, Func<Placeholder, bool> stillEligible)
    {
        lock (_gate)
        {
            _asked.Add(path);
        }

        BeforeRelease?.Invoke(path);

        lock (_gate)
        {
            return Judged(path, resolvedPath, stillEligible);
        }
    }

    /// <summary>What a release of <paramref name="path"/> comes to, judged and acted on under the lock.</summary>
    private ReleaseAnswer Judged(string path, VolumeDevicePath resolvedPath, Func<Placeholder, bool> stillEligible)
    {
        switch (_entries.GetValueOrDefault(path))
        {
            case null:
                return new ReleaseAnswer(ReleaseResult.Gone);

            case not null when new VolumeDevicePath(ResolvedFile(path)) != resolvedPath:
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case { Refused: true }:
                return new ReleaseAnswer(ReleaseResult.Refused);

            case { Placeholder: null }:
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case var entry when !stillEligible(entry.Placeholder!):
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case var entry:
                _entries[path] = entry with { Placeholder = entry.Placeholder! with { Pin = PinState.Unpinned } };
                _unpinned.Add(path);
                return new ReleaseAnswer(ReleaseResult.Requested, entry.Placeholder!.OnDiskBytes);
        }
    }

    /// <summary>Where a file's handle would resolve to: its own redirection, or its place under its root's location.</summary>
    private string ResolvedFile(string path) =>
        _redirected.GetValueOrDefault(path)
        ?? _rootLocations
            .Where(root => LongPath.Contains(root.Key, path))
            .Select(root => Path.Join(root.Value, Path.GetRelativePath(root.Key, path)))
            .FirstOrDefault(path);

    private FakeCloudFiles Put(Entry entry)
    {
        lock (_gate)
        {
            _entries[entry.Path] = entry;
        }

        return this;
    }

    private static Placeholder Placeholder(long onDisk) => new(onDisk, 0, InSync: true, PinState.Unspecified, LongAgo);

    private enum EntryKind
    {
        Placeholder,
        Plain,
        OtherLink,
    }

    private sealed record Entry(string Path, bool IsDirectory, EntryKind Kind, Placeholder? Placeholder, bool Refused = false);
}
