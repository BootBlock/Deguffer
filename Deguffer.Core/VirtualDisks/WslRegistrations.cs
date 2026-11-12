using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The virtual disks of the WSL distributions registered by the user signed in to Windows and by the
/// account Deguffer runs as, read from WSL's own registration under
/// <c>Software\Microsoft\Windows\CurrentVersion\Lxss</c> in each account's hive.
///
/// <para><b>Both hives, because this is a refusal.</b> WSL registers a distribution in the hive of the
/// account that installed it. Elevated as another account, <c>HKEY_CURRENT_USER</c> is that account's
/// hive, and reading it alone would leave the signed-in user's disks unrecognised as a virtual
/// machine's storage. See <see cref="IUserEnvironment.RunningAccountHive"/>.</para>
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

    /// <summary>
    /// The tools that register WSL distributions of their own, each recognising its own by name, asked in
    /// this order. A more particular name goes before a broader one: Podman's networking distribution
    /// before the prefix every Podman distribution shares. A distribution none of them claims is the
    /// user's own.
    /// </summary>
    private static readonly IReadOnlyList<DiskOwner> DistributionOwners =
    [
        DockerDesktopData.Instance,
        DockerDesktopSystem.Instance,
        RancherDesktopData.Instance,
        RancherDesktopSystem.Instance,
        PodmanNetworking.Instance,
        PodmanMachine.Instance,
    ];

    /// <summary>
    /// Every WSL 2 distribution's disk, the signed-in user's first and each hive's in the order the
    /// registry lists them, each marked with the tool that owns it, because the route that frees space
    /// in it is that tool's.
    /// </summary>
    public static IReadOnlyList<VirtualDisk> Read(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        IEnumerable<IUserHive> hives = environment.RunningAccountHive is { } running
            ? [environment.UserHive, running]
            : [environment.UserHive];

        var disks = new List<VirtualDisk>();

        foreach (var hive in hives)
        {
            foreach (var subKey in hive.ReadSubKeyNames(LxssKey))
            {
                // WSL keeps other keys beside the distributions, and only a distribution's is named by
                // its identifier.
                if (!Guid.TryParse(subKey, out _))
                {
                    continue;
                }

                if (Read(hive, $@"{LxssKey}\{subKey}") is { } disk)
                {
                    disks.Add(disk);
                }
            }
        }

        return disks;
    }

    private static VirtualDisk? Read(IUserHive hive, string key)
    {
        var name = hive.ReadValue(key, "DistributionName");
        var flags = hive.ReadNumber(key, "Flags");
        var state = hive.ReadNumber(key, "State");

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
        if (LongPath.Configured(hive.ReadValue(key, "BasePath")) is not { } basePath)
        {
            return null;
        }

        var file = hive.ReadValue(key, "VhdFileName") is { Length: > 0 } named
            ? named
            : DefaultDiskName;

        // A file name, never a path: a value carrying a separator or a ".." is not one WSL wrote.
        if (file != Path.GetFileName(file) || file is "." or "..")
        {
            return null;
        }

        var owner = DistributionOwners.FirstOrDefault(tool => tool.ClaimsDistribution(name)) ?? WslDistributionOwner.Instance;

        return new VirtualDisk(Path.Combine(basePath, file), owner, name);
    }
}
