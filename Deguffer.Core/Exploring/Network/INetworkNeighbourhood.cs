namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// The computers the shell's own Network folder lists (<c>FOLDERID_NetworkFolder</c>), which is the
/// list File Explorer shows under Network: what network discovery has found. Behind a seam so a test
/// describes the computers, and never waits on discovery.
/// </summary>
public interface INetworkNeighbourhood
{
    /// <summary>
    /// The computer names, without the leading separators. Only computers reached as
    /// <c>\\name</c> are listed: the folder also holds media servers and devices, which have no
    /// shares to scan. Blocks for as long as discovery takes, and stops between entries once
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    NetworkListing<string> Computers(CancellationToken ct);
}
