namespace Deguffer.Core.Providers;

/// <summary>
/// What Explore may remove of one Scoop's (§7.1), declared to match what the plan protects (§5.2).
/// The root recognises nothing, the cache recognises the downloads by name, and each settings file
/// is refused, because it can hold a GitHub token and a proxy password.
/// </summary>
internal static class ScoopToolRoots
{
    private const string RootReason =
        "This is Scoop's own folder, holding the apps it installed, their data and its buckets. "
        + "Deguffer removes nothing here except Scoop's downloads, through Scoop's own command.";

    private const string DeclinedRootReason =
        "Scoop is set to keep its apps, their data and its buckets here, so Deguffer removes none of them.";

    /// <summary>The folders in Scoop's root, none of which is cache, and what each holds.</summary>
    public static IReadOnlyList<(string Name, string Holds)> RootFolders { get; } =
    [
        ("apps", "The apps Scoop installed, and every version of each."),
        ("persist", "Settings and data Scoop keeps for installed apps across updates."),
        ("buckets", "The bucket manifests Scoop installs from."),
        ("shims", "The commands Scoop puts on your PATH, Scoop's own included."),
    ];

    /// <summary>A Scoop whose root, cache and settings files are these.</summary>
    public static IReadOnlyList<ToolRoot> Of(string root, string cache, IReadOnlyList<string> configFiles) =>
    [
        Root(root),

        new ToolRoot(
            cache,
            "This is Scoop's download cache. Deguffer clears it with Scoop's own command, and removes "
            + "only the downloads Scoop made from here.",
            static child => child.Kind == ChildKind.File && ScoopCacheEntries.IsDownloadName(child.Name)),

        .. Settings(configFiles),
    ];

    /// <summary>The Scoop <paramref name="resolution"/> found, or nothing where none was followed.</summary>
    public static IReadOnlyList<ToolRoot> Of(ScoopResolution? resolution) => resolution switch
    {
        ScoopResolution.Followed { Layout: var layout } => Of(layout.Root, layout.Cache, layout.ConfigFiles),

        // A root such as the profile is the user's as well, so it claims only what Scoop keeps in it.
        // See ToolRoot.Sparing.
        ScoopResolution.RootDeclined { Layout: var layout } =>
        [
            ToolRoot.Sparing(layout.Root, DeclinedRootReason, RootFolders.Select(folder => folder.Name)),
            .. Settings(layout.ConfigFiles),
        ],

        // No root at the cache: everything Scoop writes there may go, so it would recognise nearly the
        // whole folder, and roots at one folder are pooled, which would lift every other refusal there.
        ScoopResolution.CacheDeclined { Layout: var layout } => [Root(layout.Root), .. Settings(layout.ConfigFiles)],

        _ => [],
    };

    private static ToolRoot Root(string root) => new(root, RootReason, static _ => false);

    private static IEnumerable<ToolRoot> Settings(IReadOnlyList<string> configFiles) =>
        configFiles.Select(file => new ToolRoot(
            file,
            "This is Scoop's settings file, and it can hold a GitHub token and a proxy password. "
            + "Deguffer never removes it.",
            static _ => false));
}
