using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The virtual disks of the WSL distributions this account has registered, read from WSL's own
/// registration under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c>.
///
/// <para>Microsoft's own page on WSL disk space finds a distribution's disk this way: the subkey whose
/// <c>DistributionName</c> matches, then <c>ext4.vhdx</c> in its <c>BasePath</c>. WSL's source names the
/// disk file in <c>VhdFileName</c> where it is not that default, which is how an imported <c>.vhd</c>
/// is recorded. The WSL team has said the layout may change without notice, so a value missing or of
/// another type is read as "this registration names no disk" and never guessed at.</para>
/// </summary>
public static class WslRegistrations
{
    public const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    /// <summary>The file WSL names a distribution's disk when <c>VhdFileName</c> is absent.</summary>
    public const string DefaultDiskName = "ext4.vhdx";

    /// <summary>
    /// The bit of <c>Flags</c> that says the distribution runs in WSL 2's virtual machine. A WSL 1
    /// distribution keeps its files in a <c>rootfs</c> folder and has no disk, so without this bit
    /// there is nothing to report rather than a disk that is missing.
    /// </summary>
    private const int VirtualMachineFlag = 0x8;

    /// <summary>
    /// WSL's <c>State</c> values for a distribution whose disk is there to describe: installed,
    /// running, converting between versions, exporting and compacting. Installing and uninstalling
    /// are left out, because the disk is then still being made or already going.
    /// </summary>
    private static readonly HashSet<int> SettledStates = [1, 2, 5, 6, 7];

    /// <summary>The names Docker Desktop registers its own distributions under.</summary>
    private const string DockerSystemDistribution = "docker-desktop";

    private const string DockerDataDistribution = "docker-desktop-data";

    /// <summary>
    /// Every WSL 2 distribution's disk, in the order the registry lists them. Docker Desktop's two
    /// distributions are marked as Docker's, because the route that frees space in them is Docker's.
    /// </summary>
    public static IReadOnlyList<VirtualDisk> Read(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var disks = new List<VirtualDisk>();

        foreach (var subKey in environment.ReadCurrentUserRegistrySubKeyNames(LxssKey))
        {
            // WSL keeps other keys beside the distributions, and only a distribution's is named by
            // its identifier.
            if (!Guid.TryParse(subKey, out _))
            {
                continue;
            }

            if (Read(environment, $@"{LxssKey}\{subKey}") is { } disk)
            {
                disks.Add(disk);
            }
        }

        return disks;
    }

    private static VirtualDisk? Read(IUserEnvironment environment, string key)
    {
        var name = environment.ReadCurrentUserRegistryValue(key, "DistributionName");
        var flags = environment.ReadCurrentUserRegistryNumber(key, "Flags");
        var state = environment.ReadCurrentUserRegistryNumber(key, "State");

        if (string.IsNullOrWhiteSpace(name)
            || flags is not { } bits
            || (bits & VirtualMachineFlag) == 0
            || state is not { } settled
            || !SettledStates.Contains(settled))
        {
            return null;
        }

        // Configured takes off the \\?\ prefix WSL has been seen to write here, so the path compares
        // equal to the one Explore draws.
        if (LongPath.Configured(environment.ReadCurrentUserRegistryValue(key, "BasePath")) is not { } basePath)
        {
            return null;
        }

        var file = environment.ReadCurrentUserRegistryValue(key, "VhdFileName") is { Length: > 0 } named
            ? named
            : DefaultDiskName;

        // A file name, never a path: a value carrying a separator or a ".." is not one WSL wrote.
        if (file != Path.GetFileName(file) || file is "." or "..")
        {
            return null;
        }

        var kind = name.Equals(DockerDataDistribution, StringComparison.OrdinalIgnoreCase) ? VirtualDiskKind.DockerData
            : name.Equals(DockerSystemDistribution, StringComparison.OrdinalIgnoreCase) ? VirtualDiskKind.DockerSystem
            : VirtualDiskKind.WslDistribution;

        return new VirtualDisk(Path.Combine(basePath, file), kind, name);
    }
}
