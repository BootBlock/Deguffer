namespace Deguffer.Core.Exploring.Network;

/// <summary>Where a suggestion came from, which the box states beside it so a list of paths reads as groups.</summary>
public enum NetworkGroup
{
    MappedDrive,
    Connected,
    Remembered,
    Recent,

    /// <summary>The shell's Network folder: what discovery found.</summary>
    Network,

    /// <summary>Added by name in the network browser.</summary>
    Added,

    /// <summary>Listed by the computer or the folder named in the box.</summary>
    Listed,
}

/// <summary>One suggestion in the Path… box.</summary>
public sealed record NetworkSuggestion(NetworkPlace Place, NetworkGroup Group)
{
    /// <summary>
    /// What Tab completes the box to: the place and a separator, which is the next stage, so the
    /// computer's shares or the folder's folders are listed at once.
    /// </summary>
    public string Completion => Place.Path.TrimEnd('\\') + @"\";
}

/// <summary>
/// What the Path… box suggests for each stage of what has been typed (<see cref="NetworkQuery"/>),
/// in what order, and what is left out. Pure: the sources' answers are handed in, so each stage is
/// proved on answers a test describes.
/// </summary>
public static class NetworkSuggestions
{
    /// <summary>
    /// The empty box: mapped drives, connected shares, remembered connections, recent folders, the
    /// Network folder's computers and the computers added by name, in that order, each place once.
    /// </summary>
    public static IReadOnlyList<NetworkSuggestion> Everything(NetworkSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var mapped = sources.MappedDrives();
        var shares = sources.Shares();

        IEnumerable<NetworkSuggestion> all =
        [
            .. mapped.Where(use => use.IsConnected).Select(use => new NetworkSuggestion(use.Place, NetworkGroup.MappedDrive)),
            .. shares.Where(use => use.IsConnected).Select(use => new NetworkSuggestion(use.Place, NetworkGroup.Connected)),
            .. mapped.Concat(shares).Where(use => !use.IsConnected).Select(use => new NetworkSuggestion(use.Place, NetworkGroup.Remembered)),
            .. sources.Recent.Select(path => new NetworkSuggestion(NetworkSources.RecentFolder(path), NetworkGroup.Recent)),
            .. sources.Computers.Entries.Select(name => new NetworkSuggestion(NetworkSources.Computer(name), NetworkGroup.Network)),
            .. sources.Added.Select(name => new NetworkSuggestion(NetworkSources.Computer(name), NetworkGroup.Added)),
        ];

        return [.. all.DistinctBy(suggestion => suggestion.Place.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary><c>\\</c> typed: every computer this account is known to reach, narrowed by <paramref name="text"/>.</summary>
    public static IReadOnlyList<NetworkSuggestion> Computers(NetworkSources sources, string text)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var found = new HashSet<string>(sources.Computers.Entries, StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(sources.Added, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. NetworkMatch.Narrow(sources.AllComputers(), computer => computer.Name, text)
                .Select(computer => new NetworkSuggestion(
                    computer,
                    found.Contains(computer.Name) ? NetworkGroup.Network
                    : added.Contains(computer.Name) ? NetworkGroup.Added
                    : NetworkGroup.Connected)),
        ];
    }

    /// <summary><c>\\server\</c> typed: the shares it offers (<see cref="ShareFilter"/>), narrowed by <paramref name="text"/>.</summary>
    public static IReadOnlyList<NetworkSuggestion> Shares(string server, NetworkListing<ListedShare> listed, string text)
    {
        ArgumentNullException.ThrowIfNull(listed);

        return Listed(ShareFilter.Offered(server, listed.Entries), text);
    }

    /// <summary><c>\\server\share\...\</c> typed: the folders one level in, narrowed by <paramref name="text"/>.</summary>
    public static IReadOnlyList<NetworkSuggestion> Folders(string parent, NetworkListing<string> listed, string text)
    {
        ArgumentNullException.ThrowIfNull(listed);

        return Listed(NetworkTree.Folders(parent, listed.Entries), text);
    }

    private static NetworkSuggestion[] Listed(IReadOnlyList<NetworkPlace> places, string text) =>
        [.. NetworkMatch.Narrow(places, place => place.Name, text).Select(place => new NetworkSuggestion(place, NetworkGroup.Listed))];
}
