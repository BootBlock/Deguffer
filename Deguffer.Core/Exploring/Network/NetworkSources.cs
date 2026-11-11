namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// What the sources the box and the tree start from have answered so far, and the places Explore
/// remembers. A source that has not answered yet is <see cref="NetworkListing{T}.Empty"/>, so the
/// others are shown without waiting for it.
/// </summary>
/// <param name="Connected">The shares this account is connected to now.</param>
/// <param name="Remembered">The connections Windows reconnects at sign-in.</param>
/// <param name="Computers">The computers the shell's Network folder lists.</param>
/// <param name="Recent">The folders Explore scanned recently, most recent first.</param>
/// <param name="Added">The computers added by name in the network browser.</param>
public sealed record NetworkSources(
    NetworkListing<NetworkConnection> Connected,
    NetworkListing<NetworkConnection> Remembered,
    NetworkListing<string> Computers,
    IReadOnlyList<string> Recent,
    IReadOnlyList<string> Added)
{
    /// <summary>Only what Explore remembers, before any source has answered.</summary>
    public static NetworkSources Remembering(IReadOnlyList<string> recent, IReadOnlyList<string> added) =>
        new(NetworkListing<NetworkConnection>.Empty, NetworkListing<NetworkConnection>.Empty, NetworkListing<string>.Empty, recent, added);

    /// <summary>
    /// The shares this account uses that are mapped to a letter: connected first, then remembered
    /// and not connected now, each share once.
    /// </summary>
    public IReadOnlyList<NetworkUse> MappedDrives() => Uses(lettered: true);

    /// <summary>The shares this account uses with no letter, in the same order and on the same terms.</summary>
    public IReadOnlyList<NetworkUse> Shares() => Uses(lettered: false);

    /// <summary>
    /// Every computer this account is known to reach: the Network folder's, the ones added by name,
    /// and the ones its connections and recent folders are on, each once, in that order.
    /// </summary>
    public IReadOnlyList<NetworkPlace> AllComputers()
    {
        var connected = Connected.Entries.Concat(Remembered.Entries).Select(connection => NetworkNames.ServerOf(connection.Remote));
        var servers = Computers.Entries
            .Concat(Added)
            .Concat(connected.OfType<string>())
            .Concat(Recent.Select(NetworkNames.ServerOf).OfType<string>());

        return [.. servers.Distinct(StringComparer.OrdinalIgnoreCase).Select(Computer)];
    }

    /// <summary>A computer by name, as every list here shows one.</summary>
    public static NetworkPlace Computer(string name) => new(NetworkNames.Computer(name), name, NetworkPlaceKind.Computer);

    /// <summary>A recently scanned folder, named by its whole path, which is what tells two recent folders apart.</summary>
    public static NetworkPlace RecentFolder(string path) => new(path, path, NetworkPlaceKind.Recent);

    private List<NetworkUse> Uses(bool lettered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uses = new List<NetworkUse>();
        var all = Connected.Entries.Select(connection => (connection, IsConnected: true))
            .Concat(Remembered.Entries.Select(connection => (connection, IsConnected: false)));

        foreach (var (connection, isConnected) in all)
        {
            if (connection.Local is not null == lettered && seen.Add(connection.Remote))
            {
                uses.Add(new NetworkUse(Place(connection), isConnected));
            }
        }

        return uses;
    }

    private static NetworkPlace Place(NetworkConnection connection) =>
        new(
            connection.Remote,
            NetworkNames.LastName(connection.Remote),
            connection.Local is null ? NetworkPlaceKind.Share : NetworkPlaceKind.MappedDrive,
            connection.Local);
}

/// <summary>A share this account uses, and whether it is connected now or only remembered.</summary>
public sealed record NetworkUse(NetworkPlace Place, bool IsConnected);
