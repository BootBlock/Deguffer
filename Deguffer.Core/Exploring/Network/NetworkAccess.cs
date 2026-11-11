namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The four sources the Path… box and the network browser read, one seam each, handed over together
/// because every lookup session reads all four.
/// </summary>
/// <param name="Connections">See <see cref="INetworkConnections"/>.</param>
/// <param name="Neighbourhood">See <see cref="INetworkNeighbourhood"/>.</param>
/// <param name="Shares">See <see cref="IShareLister"/>.</param>
/// <param name="Folders">See <see cref="IFolderLister"/>.</param>
public sealed record NetworkAccess(
    INetworkConnections Connections,
    INetworkNeighbourhood Neighbourhood,
    IShareLister Shares,
    IFolderLister Folders)
{
    /// <summary>This machine's own sources, which the app runs with (G5).</summary>
    public static NetworkAccess Machine { get; } =
        new(WNetConnections.Default, ShellNetworkFolder.Default, NetShareLister.Default, LongPathFolderLister.Default);
}
