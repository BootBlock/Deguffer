namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A Podman machine on WSL: the Linux Podman runs containers in, with every image, container and volume
/// it keeps. Podman's own command frees space inside it, and WSL's compacts the disk.
///
/// <para><b>Recognised by the name Podman gives it.</b> Podman registers a machine as a WSL distribution
/// named <c>podman-</c> and the machine's name, unless that name already begins with <c>podman</c>, so
/// the default machine is <c>podman-machine-default</c>. containers/podman
/// <c>pkg/machine/wsl/machine.go</c> names the distribution with <c>env.WithPodmanPrefix</c>
/// (<c>pkg/machine/env/dir.go</c>), and <c>DefaultMachineName</c> is in
/// <c>pkg/machine/define/config.go</c>. Every distribution Podman makes therefore begins with
/// <c>podman</c>, and <see cref="PodmanNetworking"/> is asked before this one.</para>
///
/// <para><b>The route.</b> Podman's command on Windows "remotely communicates with the podman service
/// running in the guest environment" (<c>docs/tutorials/podman-for-windows.md</c>), and
/// <c>podman system prune</c> removes unused containers, pods, networks and images, and volumes only when
/// asked (<c>podman-system-prune.1.md</c>). Podman's documentation names no compaction, so the disk's is
/// WSL's, as for any distribution.</para>
/// </summary>
public sealed record PodmanMachine : DiskOwner
{
    public static PodmanMachine Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.StartsWith("podman", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => $"the Podman machine '{disk.Distribution}'";

    public override string Summary(VirtualDisk disk) =>
        $"The virtual disk of the Podman machine '{disk.Distribution}': every image, container and volume Podman "
        + "keeps in it. It grows as Podman writes and does not shrink by itself. "
        + VirtualDiskRoutes.InsideIsNotOnTheDrive;

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "To make it smaller, remove what Podman no longer needs first, with 'podman system prune' while the "
        + "machine is running: Podman's command on Windows works through the machine. Unused volumes hold data, "
        + "and go only when '--volumes' is added." + VirtualDiskRoutes.WslCompaction(disk, isSparse);

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Deleting it removes the Podman machine with every image, container and volume in it, and the supported "
        + "way to make it smaller is 'podman system prune' and then "
        + $"'wsl --manage {VirtualDiskRoutes.Quoted(disk.Distribution)} --compact'.";
}

/// <summary>
/// <c>podman-net-usermode</c>, the distribution Podman makes for user-mode networking when a machine is
/// set to use it. containers/podman <c>pkg/machine/wsl/declares.go</c> names it, and
/// <c>pkg/machine/wsl/usermodenet.go</c> makes it. It carries Podman's networking rather than the user's
/// containers, so there is nothing in it to prune.
/// </summary>
public sealed record PodmanNetworking : DiskOwner
{
    public static PodmanNetworking Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.Equals("podman-net-usermode", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => "Podman's user-mode networking distribution";

    public override string Summary(VirtualDisk disk) =>
        "The virtual disk of the distribution Podman makes for user-mode networking, which holds no containers.";

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "Podman makes this distribution itself for its networking, so there is nothing in it to prune.";

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Podman needs it for user-mode networking and makes it itself, so there is nothing here to reclaim.";
}
