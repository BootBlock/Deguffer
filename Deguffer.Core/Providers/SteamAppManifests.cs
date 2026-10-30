using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What Steam's own records say about one game.</summary>
/// <param name="Id">The game's Steam application id.</param>
/// <param name="Name">The name Steam shows for it, or null where no manifest said.</param>
/// <param name="IsInstalled">
/// Whether any library holds the game's manifest. Null where that cannot be said: a library the list
/// did not name, a library that could not be reached, or a manifest Windows would not describe, may
/// hold it.
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
/// <para>Memoised per id for the life of one planning pass (G4), because a game's cache can sit in
/// more than one library and each library is asked about the same game.</para>
///
/// <para><b>"Not installed" is a claim about every library.</b> A library on a drive that is not
/// connected answers "no manifest here" for every game, because Windows reads a missing drive as an
/// absent path. So a library that could not be reached is never searched and never counted as
/// saying no: a game whose manifest is not found elsewhere is then of unknown standing, and
/// <see cref="UnreachedLibraryNotes"/> names the library that made it so.</para>
/// </summary>
internal sealed class SteamAppManifests(SteamLibraries libraries)
{
    /// <summary>Far past any manifest Steam writes, which run to a few kilobytes.</summary>
    private const int MaximumBytes = 256 * 1024;

    /// <summary>Every character a single folder name cannot hold, the separators among them.</summary>
    private static readonly char[] NotInAFolderName = Path.GetInvalidFileNameChars();

    private readonly Dictionary<string, SteamApp> _described = new(StringComparer.Ordinal);

    /// <summary>Each library that left at least one game's standing unknown, in list order.</summary>
    private readonly List<string> _unreached = [];

    /// <summary>The manifest's name, relative to a library, for the application <paramref name="id"/>.</summary>
    public static string RelativePath(string id) => Path.Combine("steamapps", $"appmanifest_{id}.acf");

    public SteamApp Describe(string id)
    {
        if (_described.TryGetValue(id, out var known))
        {
            return known;
        }

        var unknowable = !libraries.IsComplete;
        List<string>? unreached = null;

        foreach (var library in libraries.Folders)
        {
            if (library.Presence is not PathPresence.Present)
            {
                (unreached ??= []).Add(library.Folder);
                continue;
            }

            var manifest = Path.Combine(library.Folder, RelativePath(id));

            switch (LongPath.ProbeFile(manifest))
            {
                case PathPresence.Present:
                    return _described[id] = new SteamApp(id, NameIn(manifest), IsInstalled: true);

                case PathPresence.Refused:
                    unknowable = true;
                    break;
            }
        }

        foreach (var library in unreached ?? [])
        {
            if (!_unreached.Contains(library, StringComparer.OrdinalIgnoreCase))
            {
                _unreached.Add(library);
            }
        }

        return _described[id] = new SteamApp(
            id, Name: null, IsInstalled: unknowable || unreached is not null ? null : false);
    }

    /// <summary>
    /// The sentence owed about each library that could not be reached while some game's manifest was
    /// found in no other library, so a reader filtering on "Installed" learns why that game carries
    /// no answer. Information rather than a warning: nothing is removed because of it.
    /// </summary>
    public IReadOnlyList<PlanNote> UnreachedLibraryNotes() =>
    [
        .. _unreached.Select(library => new PlanNote(
            PlanNoteSeverity.Information,
            $"Steam lists a game library at '{LongPath.Display(library)}', and Deguffer could not look in it. "
            + "A drive that is not connected, a network share that is offline and a folder this account may "
            + "not read all do that. Its games may still be installed, so a game Deguffer found in no other "
            + "library is not called uninstalled.")),
    ];

    /// <summary>
    /// The folder each library holding the application's manifest installed it in,
    /// <c>steamapps\common\&lt;installdir&gt;</c>, with the library it was built from, and the
    /// manifests Windows would not describe or that could not be read. Not memoised: it is asked once
    /// a pass, about one application.
    /// </summary>
    /// <remarks>
    /// An <c>installdir</c> that is not one plain folder name is not taken, because Steam writes only
    /// a folder name there and anything else would lead out of the library.
    /// </remarks>
    public (IReadOnlyList<(string Library, string Folder)> Folders, IReadOnlyList<string> Unread) InstallFoldersOf(string id)
    {
        List<(string Library, string Folder)> folders = [];
        List<string> unread = [];

        foreach (var (library, presence) in libraries.Folders)
        {
            var manifest = Path.Combine(library, RelativePath(id));

            switch (presence is PathPresence.Present ? LongPath.ProbeFile(manifest) : presence)
            {
                case PathPresence.Absent:
                    continue;

                case PathPresence.Refused:
                    unread.Add(manifest);
                    continue;
            }

            if (!AppStateOf(manifest, out var appState))
            {
                unread.Add(manifest);
                continue;
            }

            if (appState?.Child("installdir")?.Value?.Trim() is { Length: > 0 } installDir
                && installDir is not ("." or "..")
                && installDir.IndexOfAny(NotInAFolderName) < 0)
            {
                folders.Add((library, Path.Combine(library, "steamapps", "common", installDir)));
            }
        }

        return (folders, unread);
    }

    /// <summary>The <c>name</c> under the manifest's <c>AppState</c> block, or null.</summary>
    private static string? NameIn(string manifest)
    {
        if (!AppStateOf(manifest, out var appState))
        {
            return null;
        }

        var name = appState?.Child("name")?.Value;

        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// The manifest's <c>AppState</c> block, which is null where the manifest has none. False where the
    /// manifest could not be read or parsed at all.
    /// </summary>
    private static bool AppStateOf(string manifest, out SteamKeyValue? appState)
    {
        appState = null;

        if (BoundedFile.Read(manifest, MaximumBytes) is not { } content
            || SteamKeyValues.Parse(Encoding.UTF8.GetString(content.Span)) is not { } entries)
        {
            return false;
        }

        appState = entries.FirstOrDefault(e => string.Equals(e.Key, "AppState", StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
