namespace Deguffer.Core.Exploring.Network;

/// <summary>The top level of the network browser's tree, in the order it is shown.</summary>
public enum NetworkRoot
{
    MappedDrives,
    ConnectedShares,
    Recent,

    /// <summary>The computers discovery finds.</summary>
    Network,

    /// <summary>The computers added by name, which covers the ones discovery cannot find.</summary>
    Added,
}

/// <summary>What a node of the tree lists when it is opened: shares for a computer, folders otherwise.</summary>
public enum NetworkChildren
{
    Shares,
    Folders,
}

/// <summary>
/// What the network browser's tree holds: its roots, what each root lists, and what a place lists
/// when it is opened. Pure, as <see cref="NetworkSuggestions"/> is: the sources' answers are handed in.
/// </summary>
public static class NetworkTree
{
    public static IReadOnlyList<NetworkRoot> Roots { get; } =
        [NetworkRoot.MappedDrives, NetworkRoot.ConnectedShares, NetworkRoot.Recent, NetworkRoot.Network, NetworkRoot.Added];

    /// <summary>
    /// What a root lists, from what the sources answered, each place once. A root fails only where
    /// every source it reads failed.
    /// </summary>
    public static NetworkListing<NetworkPlace> Listed(NetworkRoot root, NetworkSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return root switch
        {
            NetworkRoot.MappedDrives => Uses(sources.MappedDrives(), sources),
            NetworkRoot.ConnectedShares => Uses(sources.Shares(), sources),
            NetworkRoot.Recent => NetworkListing<NetworkPlace>.Listed([.. sources.Recent.Select(NetworkSources.RecentFolder)]),
            NetworkRoot.Network => sources.Computers.Failure is { } failure
                ? NetworkListing<NetworkPlace>.Failed(failure)
                : NetworkListing<NetworkPlace>.Listed(
                    [.. sources.Computers.Entries.Distinct(StringComparer.OrdinalIgnoreCase).Select(NetworkSources.Computer)]),
            NetworkRoot.Added => NetworkListing<NetworkPlace>.Listed([.. sources.Added.Select(NetworkSources.Computer)]),
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
        };
    }

    /// <summary>
    /// Whether <paramref name="root"/> is open the first time the browser is shown. Every root but
    /// Network is read from this account's own connections and preferences in a moment. Network
    /// waits on discovery, so it is opened only when the reader asks.
    /// </summary>
    public static bool OpensAtFirst(NetworkRoot root) => root is not NetworkRoot.Network;

    /// <summary>What opening <paramref name="place"/> lists.</summary>
    public static NetworkChildren ChildrenOf(NetworkPlace place) =>
        place.IsComputer ? NetworkChildren.Shares : NetworkChildren.Folders;

    /// <summary>The shares a computer lists, as nodes. See <see cref="ShareFilter"/>.</summary>
    public static NetworkListing<NetworkPlace> Shares(NetworkPlace computer, NetworkListing<ListedShare> listed)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(listed);

        return listed.Failure is { } failure
            ? NetworkListing<NetworkPlace>.Failed(failure)
            : NetworkListing<NetworkPlace>.Listed(ShareFilter.Offered(computer.Name, listed.Entries));
    }

    /// <summary>The folders a share or folder lists, as nodes.</summary>
    public static NetworkListing<NetworkPlace> Folders(NetworkPlace parent, NetworkListing<string> listed)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(listed);

        return listed.Failure is { } failure
            ? NetworkListing<NetworkPlace>.Failed(failure)
            : NetworkListing<NetworkPlace>.Listed(Folders(parent.Path, listed.Entries));
    }

    /// <summary>Folders named <paramref name="names"/> inside <paramref name="parent"/>, A to Z.</summary>
    public static IReadOnlyList<NetworkPlace> Folders(string parent, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return
        [
            .. names
                .Where(name => name.Length > 0)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => new NetworkPlace(NetworkNames.Inside(parent, name), name, NetworkPlaceKind.Folder)),
        ];
    }

    /// <summary>
    /// The place among <paramref name="children"/> on the way to <paramref name="target"/>, or null
    /// where none leads there. How the browser opens again at the place it last showed, one level at
    /// a time as each level is listed.
    /// </summary>
    public static NetworkPlace? Toward(IEnumerable<NetworkPlace> children, string target) =>
        children
            .Where(child => NetworkNames.Leads(child.Path, target))
            .MaxBy(child => child.Path.Length);

    private static NetworkListing<NetworkPlace> Uses(IReadOnlyList<NetworkUse> uses, NetworkSources sources) =>
        uses.Count == 0 && sources.Connected.Failure is { } failure && sources.Remembered.Failure is not null
            ? NetworkListing<NetworkPlace>.Failed(failure)
            : NetworkListing<NetworkPlace>.Listed([.. uses.Select(use => use.Place)]);
}
