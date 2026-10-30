namespace Deguffer.Core.Providers;

/// <summary>
/// What Conan keeps in its home and its store, as the §5.6 survivors and the §7.1 tool roots name it.
/// The home's files are <c>conan/internal/cache/home_paths.py</c>, Conan 2.34.0 (commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>).
/// </summary>
internal static class ConanDeclarations
{
    private const string DeclinedHomeReason =
        "Conan is set to keep its home here, so its configuration and package store are here too.";

    private const string DeclinedStoreReason =
        "Conan is set to keep its package store here, so its database of every recipe and package is here too.";

    /// <summary>What Conan keeps in its home beside the store, none of which the clean touches.</summary>
    private static readonly (string Name, string Reason)[] HomeContents =
    [
        ("global.conf", "Conan's configuration."),
        ("global_user.conf", "Conan's configuration."),
        ("profiles", "The profiles your builds use."),
        ("remotes.json", "The remotes Conan downloads from."),
        ("settings.yml", "Conan's settings model."),
        ("settings_user.yml", "Your additions to Conan's settings model."),
        ("extensions", "Your hooks, plugins, deployers and generators."),
        ("audit_providers.json", "Conan's audit provider configuration."),
        ("config_version.json", "Which configuration Conan installed."),
        (".local_recipes_index", "Local recipe repositories."),
        ("version.txt", "The Conan version the home was last migrated to."),
        ("sources", "Conan's backup of downloaded sources, which this clean is never asked to remove."),
    ];

    /// <summary>The home, its configuration, the store and its database, each with why it must survive.</summary>
    public static IEnumerable<(string Path, string Reason)> Survivors(string home, string store) =>
    [
        (home, "Conan's home must survive: only sources and build folders in its store are cleared."),
        .. HomeContents.Select(entry => (Path.Combine(home, entry.Name), entry.Reason)),
        (store, "Conan's package store must survive: only sources and build folders inside it are cleared."),
        (Path.Combine(store, ConanStoreContents.Database), "Conan's database of every recipe and package in the store."),
    ];

    public static ToolRoot Home(string home) => new(
        home,
        "This is Conan's home. Its configuration, your profiles and remotes, and the package store "
        + "that holds everything Conan built or downloaded are all in here. Clearing its sources and "
        + "build folders is offered on the Storage page, where Conan's own command does it.",
        static _ => false);

    public static ToolRoot Store(string store) => new(
        store,
        "This is Conan's package store, which holds every recipe and package binary Conan has, and the "
        + "database that lists them. Clearing its sources and build folders is offered on the Storage "
        + "page, where Conan's own command does it.",
        static _ => false);

    /// <summary>
    /// A home Conan is set to keep in a folder that is not examined as Conan's, where only Conan's own
    /// entries are refused. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public static ToolRoot DeclinedHome(string home) => ToolRoot.Sparing(
        home,
        DeclinedHomeReason,
        [.. HomeContents.Select(entry => entry.Name), ConanConfigOutput.DefaultStoreFolder]);

    /// <summary>The same for a store, where the database is the entry that is Conan's.</summary>
    public static ToolRoot DeclinedStore(string store) => ToolRoot.Sparing(store, DeclinedStoreReason, [ConanStoreContents.Database]);
}
