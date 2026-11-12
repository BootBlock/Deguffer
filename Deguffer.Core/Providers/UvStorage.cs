using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where uv keeps what it owns on Windows, and which of it a clean of the cache must leave standing.
///
/// <para>uv splits itself across two profiles. Only the cache is under <c>%LOCALAPPDATA%\uv</c>. The
/// persistent state (the tools <c>uv tool install</c> put there, the interpreters uv manages and the
/// plain-text credential store <c>uv auth login</c> writes) and the user's <c>uv.toml</c> are in the
/// roaming profile, under <c>%APPDATA%\uv</c>. An install from before late 2024 keeps that state one
/// level down, in <c>%APPDATA%\uv\data</c>, and uv still uses that folder wherever it exists
/// (<c>uv-state</c>'s <c>StateStore::from_settings</c>).</para>
///
/// <para><c>UV_TOOL_DIR</c> and <c>UV_PYTHON_INSTALL_DIR</c> move the two state folders anywhere, so
/// uv is asked for them, and the documented layout is only the answer where uv gives none.</para>
/// </summary>
/// <param name="Cache">The cache <c>uv cache dir</c> reported, or the documented default.</param>
/// <param name="Tools">The folder <c>uv tool dir</c> reported, or its default in the first state root.</param>
/// <param name="Python">The folder <c>uv python dir</c> reported, or its default in the first state root.</param>
/// <param name="StateRoots">
/// The folder uv keeps its state in: one, or both candidates where Windows would not say whether the
/// legacy folder exists, because a refusal is no evidence that it is absent.
/// </param>
/// <param name="LocalRoot"><c>%LOCALAPPDATA%\uv</c>, which holds the cache.</param>
/// <param name="RoamingRoot"><c>%APPDATA%\uv</c>, which holds the configuration and, by default, the state.</param>
internal sealed record UvStorage(
    AskedLocation Cache,
    AskedLocation Tools,
    AskedLocation Python,
    IReadOnlyList<string> StateRoots,
    string LocalRoot,
    string RoamingRoot)
{
    /// <summary>The folder an install from before late 2024 keeps its state in.</summary>
    public static string LegacyStateRoot(string roamingRoot) => Path.Combine(roamingRoot, "data");

    /// <summary>
    /// The state root uv uses, given what Windows said about <see cref="LegacyStateRoot"/>. Both candidates
    /// where it would not say, so the survivors cover whichever one uv is using.
    /// </summary>
    public static IReadOnlyList<string> StateRootsFor(string roamingRoot, PathPresence legacy) => legacy switch
    {
        PathPresence.Present => [LegacyStateRoot(roamingRoot)],
        PathPresence.Absent => [roamingRoot],
        _ => [LegacyStateRoot(roamingRoot), roamingRoot],
    };

    /// <summary>
    /// §5.6: every path a clean of the cache must leave standing, each with why. The cache itself is not
    /// one, because <c>uv cache clean</c> removes it and uv makes it again on the next use.
    /// </summary>
    public IReadOnlyList<(string Path, string Reason)> Survivors =>
    [
        (LocalRoot, "uv's local folder must survive. Only the cache within it is cleared."),
        .. FoldersFor(Tools, "tools").Select(tools => (tools, "Command-line tools installed with 'uv tool install'.")),
        .. FoldersFor(Python, "python").Select(python => (python, "Python interpreters uv downloaded and manages.")),
        .. StateRoots.Select(root => (Path.Combine(root, "credentials"),
            "uv's credential store, which holds the passwords and tokens 'uv auth login' saved.")),
        .. StateRoots.Select(root => (root, "uv's state folder, which holds its tools, interpreters and credentials.")),
        (Path.Combine(RoamingRoot, "uv.toml"), "uv's user configuration."),
    ];

    /// <summary>
    /// The survivor a clean of <see cref="Cache"/> would take with it, or null where it takes none.
    ///
    /// <para><c>uv cache clean</c> removes the whole cache folder, and <c>cache-dir</c> can name any folder.
    /// Set to the state root, or to a folder holding it, the clean would uninstall every tool and
    /// interpreter. A cache inside one of uv's own state folders would be removed from under it.</para>
    /// </summary>
    public string? CacheConflict()
    {
        var cache = Path.TrimEndingDirectorySeparator(Cache.Path);
        var owned = FoldersFor(Tools, "tools").Concat(FoldersFor(Python, "python")).Concat(StateRoots).Append(RoamingRoot);

        return owned.FirstOrDefault(folder => LongPath.Contains(Path.TrimEndingDirectorySeparator(folder), cache))
            ?? Survivors.Select(survivor => survivor.Path).FirstOrDefault(survivor => LongPath.Contains(cache, survivor));
    }

    /// <summary>The folder uv reported, or the default in every candidate state root where it did not.</summary>
    private IEnumerable<string> FoldersFor(AskedLocation asked, string bucket) =>
        asked.Answered ? [asked.Path] : StateRoots.Select(root => Path.Combine(root, bucket));
}
