using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Sync roots and placeholders held in memory, for the rules that decide which local copies go.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real <see cref="CloudFiles"/> walks and unpins
/// the cloud accounts of whoever runs the suite. <see cref="CloudFilesTests"/> covers the real calls
/// against a scratch sync root of its own.</para>
///
/// <para><b>It behaves as the API was observed to, not as the rules would like.</b>
/// <see cref="Release"/> unpins whatever the caller's predicate accepts, because <c>CfSetPinState</c>
/// refuses nothing: every rule under test is then Deguffer's own.</para>
/// </summary>
public sealed class FakeCloudFiles : ICloudFiles
{
    private readonly List<SyncRoot> _roots = [];
    private readonly Dictionary<string, SyncProviderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _redirected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rootLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make <see cref="SyncRoots"/> answer as Windows does when it will not list them.</summary>
    public bool RefusesToListRoots { get; set; }

    /// <summary>Well before any guard window a test sets, so a file is old unless it says otherwise.</summary>
    public static readonly long LongAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>Every path a release was asked about, in order, whatever came of it.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>The paths whose pin a release changed, in order.</summary>
    public List<string> Unpinned { get; } = [];

    /// <summary>
    /// Run as each release begins, before the file is looked at, so a test can change the disk between
    /// the preview and the moment a file is judged again.
    /// </summary>
    public Action<string>? BeforeRelease { get; set; }

    public FakeCloudFiles Root(string id, string path, string displayName, SyncProviderState state = SyncProviderState.Running)
    {
        _roots.Add(new SyncRoot(id, path, displayName));
        _states[path] = state;
        _entries[path] = new Entry(path, IsDirectory: true, EntryKind.Placeholder, Placeholder(0));
        return this;
    }

    public FakeCloudFiles Folder(string path, PinState pin = PinState.Unspecified)
    {
        _entries[path] = new Entry(path, IsDirectory: true, EntryKind.Placeholder, Placeholder(0) with { Pin = pin });
        return this;
    }

    /// <summary>A folder that is not a placeholder, which a sync app may still keep placeholders under.</summary>
    public FakeCloudFiles PlainFolder(string path)
    {
        _entries[path] = new Entry(path, IsDirectory: true, EntryKind.Plain, null);
        return this;
    }

    /// <summary>A junction or symbolic link to a folder, which is never entered.</summary>
    public FakeCloudFiles Link(string path)
    {
        _entries[path] = new Entry(path, IsDirectory: true, EntryKind.OtherLink, null);
        return this;
    }

    public FakeCloudFiles File(
        string path,
        long onDisk,
        long modified = 0,
        bool inSync = true,
        PinState pin = PinState.Unspecified,
        long? newest = null)
    {
        _entries[path] = new Entry(
            path,
            IsDirectory: false,
            EntryKind.Placeholder,
            new Placeholder(onDisk, modified, inSync, pin, newest ?? LongAgo));
        return this;
    }

    /// <summary>An ordinary file inside a sync root, which no rule may touch.</summary>
    public FakeCloudFiles PlainFile(string path)
    {
        _entries[path] = new Entry(path, IsDirectory: false, EntryKind.Plain, null);
        return this;
    }

    /// <summary>An entry Windows lists and will not describe.</summary>
    public FakeCloudFiles Refuse(string path)
    {
        _entries[path] = _entries[path] with { Refused = true };
        return this;
    }

    public void Remove(string path)
    {
        foreach (var key in _entries.Keys.Where(key => LongPath.Contains(path, key)).ToList())
        {
            _entries.Remove(key);
        }
    }

    public void Change(string path, Func<Placeholder, Placeholder> change) =>
        _entries[path] = _entries[path] with { Placeholder = change(_entries[path].Placeholder!) };

    public void Stop(string root) => _states[root] = SyncProviderState.NotRunning;

    /// <summary>
    /// Make a file open somewhere else, as a folder above it turned into a junction would: the handle
    /// then resolves to <paramref name="actually"/> rather than to the file named.
    /// </summary>
    public void Redirect(string path, string actually) => _redirected[path] = actually;

    /// <summary>
    /// Make a root really live at <paramref name="location"/>, as one the user reaches through a link of
    /// their own does: every file under it then resolves under that location.
    /// </summary>
    public void Locate(string root, string location) => _rootLocations[root] = location;

    public Placeholder? PlaceholderAt(string path) => _entries.GetValueOrDefault(path)?.Placeholder;

    public IReadOnlyList<SyncRoot>? SyncRoots() => RefusesToListRoots ? null : [.. _roots];

    public string? Resolve(string path) =>
        _entries.ContainsKey(path) ? _rootLocations.GetValueOrDefault(path, path) : null;

    /// <summary>Where a file's handle would resolve to: its own redirection, or its place under its root's location.</summary>
    private string ResolvedFile(string path) =>
        _redirected.GetValueOrDefault(path)
        ?? _rootLocations
            .Where(root => LongPath.Contains(root.Key, path))
            .Select(root => Path.Join(root.Value, Path.GetRelativePath(root.Key, path)))
            .FirstOrDefault(path);

    public SyncProviderState ProviderState(string syncRoot) =>
        _states.GetValueOrDefault(syncRoot, SyncProviderState.Unknown);

    public IEnumerable<CloudEntry> List(string directory, CancellationToken ct) =>
        _entries.Values
            .Where(entry => string.Equals(Path.GetDirectoryName(entry.Path), directory, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CloudEntry(
                entry.Path,
                entry.IsDirectory,
                IsPlaceholder: entry.Kind == EntryKind.Placeholder,
                IsOtherLink: entry.Kind == EntryKind.OtherLink))
            .ToList();

    public PlaceholderReading Read(string path) => _entries.GetValueOrDefault(path) switch
    {
        null => PlaceholderReading.Absent,
        { Refused: true } => PlaceholderReading.Refused,
        var entry => new PlaceholderReading(PathPresence.Present, entry.Placeholder),
    };

    public ReleaseAnswer Release(string path, string resolvedPath, Func<Placeholder, bool> stillEligible)
    {
        Asked.Add(path);
        BeforeRelease?.Invoke(path);

        switch (_entries.GetValueOrDefault(path))
        {
            case null:
                return new ReleaseAnswer(ReleaseResult.Gone);

            case not null when !string.Equals(ResolvedFile(path), resolvedPath, StringComparison.OrdinalIgnoreCase):
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case { Refused: true }:
                return new ReleaseAnswer(ReleaseResult.Refused);

            case { Placeholder: null }:
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case var entry when !stillEligible(entry.Placeholder!):
                return new ReleaseAnswer(ReleaseResult.NoLongerEligible);

            case var entry:
                Change(path, placeholder => placeholder with { Pin = PinState.Unpinned });
                Unpinned.Add(path);
                return new ReleaseAnswer(ReleaseResult.Requested, entry.Placeholder!.OnDiskBytes);
        }
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
