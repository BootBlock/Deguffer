using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What Steam's own records say about one game.</summary>
/// <param name="Id">The game's Steam application id.</param>
/// <param name="Name">The name Steam shows for it, or null where no manifest said.</param>
/// <param name="IsInstalled">
/// Whether any library holds the game's manifest. Null where that cannot be said: a library the list
/// did not name, a library that could not be reached, or a library whose records Windows would not
/// list, may hold it.
/// </param>
public sealed record SteamApp(string Id, string? Name, bool? IsInstalled)
{
    /// <summary>The game's name where Steam recorded one, and its id otherwise.</summary>
    public string DisplayName => Name ?? $"the game with Steam app ID {Id}";

    /// <summary>
    /// The game as an item that can be kept. Keyed by the id, which is the game wherever its files
    /// are, so a game kept stays kept if the user moves it to another drive.
    /// </summary>
    public ItemIdentity Identity => new(Id, Name ?? $"Steam app {Id}");

    /// <summary>What a reader choosing between games is told about this one.</summary>
    public IReadOnlyList<ItemFacet> Facets => IsInstalled switch
    {
        true => [new ItemFacet("App ID", Id), new ItemFacet("Installed", "Yes")],
        false => [new ItemFacet("App ID", Id), new ItemFacet("Installed", "No")],
        null => [new ItemFacet("App ID", Id)],
    };
}

/// <summary>
/// Reads the manifest Steam keeps for each installed game, <c>steamapps\appmanifest_&lt;id&gt;.acf</c>
/// in whichever library holds it, to put a name to an application id.
///
/// <para>Read rather than guessed, and only for the name and the install folder. A manifest also
/// records the game's build and the account that installed it, and neither is taken. The manifest is
/// Steam's record that the game is installed, so it is never a target: the provider that asks here
/// names it as a survivor instead.</para>
///
/// <para><b>One per planning pass, shared by every Steam row</b> through
/// <see cref="SteamDiscovery.Manifests"/> (G4). The artwork row asks about every game Steam has shown
/// in the library, often a thousand or two, and the shader row asks about every game with a cache,
/// mostly the same installed games. Each library's <c>steamapps</c> is listed once, so "which games
/// does this library hold" is a set lookup rather than a probe per game per library, and each manifest
/// the listings show is read once. The rows plan concurrently, so every answer is worked out once
/// however many threads ask for it.</para>
///
/// <para><b>"Not installed" is a claim about every library.</b> A library on a drive that is not
/// connected answers "no manifest here" for every game, because Windows reads a missing drive as an
/// absent path. So a library that could not be reached, or whose <c>steamapps</c> Windows would not
/// list, is never counted as saying no: a game whose manifest is not found elsewhere is then of
/// unknown standing, and <see cref="UnreachedLibraryNotes"/> names the library that made it so.</para>
/// </summary>
internal sealed class SteamAppManifests
{
    /// <summary>Far past any manifest Steam writes, which run to a few kilobytes.</summary>
    private const int MaximumBytes = 256 * 1024;

    private const string ManifestPrefix = "appmanifest_";

    private const string ManifestExtension = ".acf";

    /// <summary>Every character a single folder name cannot hold, the separators among them.</summary>
    private static readonly char[] NotInAFolderName = Path.GetInvalidFileNameChars();

    private readonly SteamLibraries _libraries;

    /// <summary>
    /// The ids of the manifests in each library's <c>steamapps</c>, by the library's place in the list.
    /// Null where Windows would not list it. Listed on first ask, so a library nobody asks about is
    /// never listed.
    /// </summary>
    private readonly Lazy<IReadOnlySet<string>?>[] _held;

    private readonly ConcurrentDictionary<string, Lazy<Standing>> _described = new(StringComparer.Ordinal);

    /// <summary>Each manifest read, by its library's place in the list and the game's id.</summary>
    private readonly ConcurrentDictionary<(int Library, string Id), Lazy<AppManifest?>> _read = new();

    private int _listings;
    private int _reads;

    public SteamAppManifests(SteamLibraries libraries)
    {
        _libraries = libraries;
        _held = [.. libraries.Folders.Select(library => new Lazy<IReadOnlySet<string>?>(() => ManifestsIn(library.Folder)))];
    }

    /// <summary>
    /// How many <c>steamapps</c> folders were listed, for the tests that hold a pass to one listing
    /// per library.
    /// </summary>
    internal int Listings => Volatile.Read(ref _listings);

    /// <summary>How many manifests were read, for the tests that hold a pass to one read per manifest.</summary>
    internal int Reads => Volatile.Read(ref _reads);

    /// <summary>The manifest's name, relative to a library, for the application <paramref name="id"/>.</summary>
    public static string RelativePath(string id) => Path.Combine("steamapps", $"{ManifestPrefix}{id}{ManifestExtension}");

    public SteamApp Describe(string id) => StandingOf(id).App;

    /// <summary>
    /// The sentence owed about each library that could not be looked in while one of
    /// <paramref name="described"/> was found in no other library, so a reader filtering on
    /// "Installed" learns why that game carries no answer. Information rather than a warning: nothing
    /// is removed because of it.
    ///
    /// <para>Asked about the games the calling row described, rather than every game this pass
    /// described, because the object is shared: a row names only the libraries that decided something
    /// about its own games.</para>
    /// </summary>
    /// <param name="described">The games the calling row described here.</param>
    /// <param name="refusalsNamed">
    /// Whether the caller already names each library Windows would not describe, as the shader row
    /// does with <see cref="UnreadableRoot.UnreachedNote"/>, so those are left out.
    /// </param>
    public IReadOnlyList<PlanNote> UnreachedLibraryNotes(IEnumerable<SteamApp> described, bool refusalsNamed)
    {
        var decided = new bool[_held.Length];

        foreach (var app in described)
        {
            foreach (var library in StandingOf(app.Id).Unreached)
            {
                decided[library] = true;
            }
        }

        return
        [
            .. Enumerable.Range(0, decided.Length)
                .Where(i => decided[i] && !(refusalsNamed && _libraries.Folders[i].Presence is PathPresence.Refused))
                .Select(i => new PlanNote(PlanNoteSeverity.Information, WhyUnreached(_libraries.Folders[i]))),
        ];
    }

    /// <summary>
    /// The folder each library holding the application's manifest installed it in,
    /// <c>steamapps\common\&lt;installdir&gt;</c>, with the library it was built from, and the
    /// manifests that could not be looked for or read.
    /// </summary>
    public (IReadOnlyList<(string Library, string Folder)> Folders, IReadOnlyList<string> Unread) InstallFoldersOf(string id)
    {
        List<(string Library, string Folder)> folders = [];
        List<string> unread = [];

        for (var i = 0; i < _held.Length; i++)
        {
            var (library, presence) = _libraries.Folders[i];
            var manifest = Path.Combine(library, RelativePath(id));

            if (presence is PathPresence.Absent)
            {
                continue;
            }

            if (presence is PathPresence.Refused || _held[i].Value is not { } held)
            {
                unread.Add(manifest);
                continue;
            }

            if (!held.Contains(id))
            {
                continue;
            }

            if (ManifestOf(i, id) is not { } read)
            {
                unread.Add(manifest);
                continue;
            }

            if (read.InstallDir is { } installDir)
            {
                folders.Add((library, Path.Combine(library, "steamapps", "common", installDir)));
            }
        }

        return (folders, unread);
    }

    private Standing StandingOf(string id) =>
        _described.GetOrAdd(id, static (key, self) => new Lazy<Standing>(() => self.Stand(key)), this).Value;

    private Standing Stand(string id)
    {
        List<int>? unreached = null;

        for (var i = 0; i < _held.Length; i++)
        {
            if (_libraries.Folders[i].Presence is not PathPresence.Present || _held[i].Value is not { } held)
            {
                (unreached ??= []).Add(i);
                continue;
            }

            if (held.Contains(id))
            {
                return new Standing(new SteamApp(id, ManifestOf(i, id)?.Name, IsInstalled: true), []);
            }
        }

        var installed = !_libraries.IsComplete || unreached is not null ? (bool?)null : false;

        return new Standing(new SteamApp(id, Name: null, installed), unreached ?? []);
    }

    private AppManifest? ManifestOf(int library, string id) =>
        _read.GetOrAdd(
                (library, id),
                static (key, self) => new Lazy<AppManifest?>(
                    () => self.Read(Path.Combine(self._libraries.Folders[key.Library].Folder, RelativePath(key.Id)))),
                this)
            .Value;

    /// <summary>
    /// The ids of the manifests directly inside the library's <c>steamapps</c>, empty where there is no
    /// <c>steamapps</c>, and null where Windows would not list it.
    /// </summary>
    private IReadOnlySet<string>? ManifestsIn(string library)
    {
        Interlocked.Increment(ref _listings);

        var ids = FolderEntries.Of(
            Path.Combine(library, "steamapps"),
            static (ref FileSystemEntry entry) => entry.IsDirectory ? null : IdOf(entry.FileName));

        return ids?.OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The id in a manifest's file name, or null for any other name. Matched without regard to case,
    /// because Windows opens the file whatever the case of its name, so Steam reads it that way too.
    /// </summary>
    private static string? IdOf(ReadOnlySpan<char> name) =>
        name.Length > ManifestPrefix.Length + ManifestExtension.Length
        && name.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase)
            ? name[ManifestPrefix.Length..^ManifestExtension.Length].ToString()
            : null;

    private static string WhyUnreached(SteamLibrary library)
    {
        var folder = LongPath.Display(library.Folder);
        var consequence = "Its games may still be installed, so a game Deguffer found in no other library "
            + "is not called uninstalled.";

        return library.Presence switch
        {
            PathPresence.Absent =>
                $"Steam lists a game library at '{folder}', and it is not there. A drive that is not connected, "
                + $"a network share that is offline and a library moved without telling Steam all do that. {consequence}",
            PathPresence.Refused =>
                $"Windows would not say what is at '{folder}', a game library Steam lists, so Deguffer could not "
                + $"look in it. {UnreadableRoot.Causes} {consequence}",
            _ =>
                $"Windows would not let Deguffer list '{LongPath.Display(Path.Combine(library.Folder, "steamapps"))}', "
                + $"where Steam records the games installed in one of your libraries, so Deguffer could not look "
                + $"for those records. {consequence}",
        };
    }

    /// <summary>
    /// The name and install folder under the manifest's <c>AppState</c> block, or null where the
    /// manifest could not be read or parsed at all.
    /// </summary>
    /// <remarks>
    /// An <c>installdir</c> that is not one plain folder name is not taken, because Steam writes only
    /// a folder name there and anything else would lead out of the library.
    /// </remarks>
    private AppManifest? Read(string manifest)
    {
        Interlocked.Increment(ref _reads);

        if (BoundedFile.Read(manifest, MaximumBytes) is not { } content
            || SteamKeyValues.Parse(Encoding.UTF8.GetString(content.Span)) is not { } entries)
        {
            return null;
        }

        var appState = entries.FirstOrDefault(e => string.Equals(e.Key, "AppState", StringComparison.OrdinalIgnoreCase));
        var name = appState?.Child("name")?.Value;
        var installDir = appState?.Child("installdir")?.Value?.Trim();

        return new AppManifest(
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            installDir is { Length: > 0 } and not ("." or "..") && installDir.IndexOfAny(NotInAFolderName) < 0
                ? installDir
                : null);
    }

    /// <summary>A game's answer, and the libraries, by place in the list, that left it unknown.</summary>
    private sealed record Standing(SteamApp App, IReadOnlyList<int> Unreached);

    /// <summary>What is taken from one manifest.</summary>
    private sealed record AppManifest(string? Name, string? InstallDir);
}
