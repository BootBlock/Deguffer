using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Every virtual disk WSL and Docker Desktop have recorded for this account, and anything that kept
/// one from being found.
/// </summary>
/// <param name="Disks">Each disk once, WSL's registrations first.</param>
/// <param name="Problems">Why a tool's record could not be read, for the user. Usually empty.</param>
public sealed record VirtualDiskInventory(IReadOnlyList<VirtualDisk> Disks, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Reads both tools' records. Cheap enough to run where it is needed, since it is a few registry
    /// values and one small file, and not memoised here: the provider keeps it for a planning pass and
    /// Explore for the life of its page.
    /// </summary>
    public static VirtualDiskInventory Read(IUserEnvironment environment, ISystemDirectories system)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);

        var registered = WslRegistrations.Read(environment);
        var docker = DockerDesktopDisks.Read(environment, system);

        // An installation from before Docker Desktop 4.30 keeps using its docker-desktop-data
        // distribution after an upgrade, and the settings then name a data disk that was never made.
        // Reporting that one as missing would be a false alarm about a disk that is in use elsewhere.
        var keepsOlderDisk = registered.Any(disk => disk.Kind == VirtualDiskKind.DockerData);

        var disks = new List<VirtualDisk>(registered);

        foreach (var disk in docker.Disks)
        {
            if (disks.Any(known => known.Path.Equals(disk.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (keepsOlderDisk && LongPath.ProbeFile(disk.Path) == PathPresence.Absent)
            {
                continue;
            }

            disks.Add(disk);
        }

        return new VirtualDiskInventory(disks, docker.Problem is { } problem ? [problem] : []);
    }
}
