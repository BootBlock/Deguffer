using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What Steam's own records say about one game.</summary>
/// <param name="Id">The game's Steam application id.</param>
/// <param name="Name">The name Steam shows for it, or null where no manifest said.</param>
/// <param name="IsInstalled">
/// Whether any library holds the game's manifest. Null where that cannot be said: a library the list
/// did not name, or one Windows would not describe, may hold it.
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
/// </summary>
internal sealed class SteamAppManifests(SteamLibraries libraries)
{
    /// <summary>Far past any manifest Steam writes, which run to a few kilobytes.</summary>
    private const int MaximumBytes = 256 * 1024;

    /// <summary>Every character a single folder name cannot hold, the separators among them.</summary>
    private static readonly char[] NotInAFolderName = Path.GetInvalidFileNameChars();

    private readonly Dictionary<string, SteamApp> _described = new(StringComparer.Ordinal);

    /// <summary>The manifest's name, relative to a library, for the application <paramref name="id"/>.</summary>
    public static string RelativePath(string id) => Path.Combine("steamapps", $"appmanifest_{id}.acf");

    public SteamApp Describe(string id)
    {
        if (_described.TryGetValue(id, out var known))
        {
            return known;
        }

        var unknowable = !libraries.IsComplete;

        foreach (var library in libraries.Folders)
        {
            var manifest = Path.Combine(library, RelativePath(id));

            switch (LongPath.ProbeFile(manifest))
            {
                case PathPresence.Present:
                    return _described[id] = new SteamApp(id, NameIn(manifest), IsInstalled: true);

                case PathPresence.Refused:
                    unknowable = true;
                    break;
            }
        }

        return _described[id] = new SteamApp(id, Name: null, IsInstalled: unknowable ? null : false);
    }

    /// <summary>
    /// The folder each library holding the application's manifest installed it in,
    /// <c>steamapps\common\&lt;installdir&gt;</c>, and the manifests Windows would not describe or
    /// that could not be read. Not memoised: it is asked once a pass, about one application.
    /// </summary>
    /// <remarks>
    /// An <c>installdir</c> that is not one plain folder name is not taken, because Steam writes only
    /// a folder name there and anything else would lead out of the library.
    /// </remarks>
    public (IReadOnlyList<string> Folders, IReadOnlyList<string> Unread) InstallFoldersOf(string id)
    {
        List<string> folders = [];
        List<string> unread = [];

        foreach (var library in libraries.Folders)
        {
            var manifest = Path.Combine(library, RelativePath(id));

            switch (LongPath.ProbeFile(manifest))
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
                folders.Add(Path.Combine(library, "steamapps", "common", installDir));
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
