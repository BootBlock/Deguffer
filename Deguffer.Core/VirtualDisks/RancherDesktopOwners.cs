namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// <c>rancher-desktop-data</c>, the distribution Rancher Desktop keeps its persistent data in: container
/// images, volumes and Kubernetes' state, which live under <c>/var/lib</c> there.
///
/// <para>Grounded in rancher-sandbox/rancher-desktop. <c>pkg/rancher-desktop/backend/wsl.ts</c> names the
/// distribution <c>DATA_INSTANCE_NAME</c> and lists <c>/var/lib</c> among the "directories that are in the
/// data distribution (persisted across version upgrades)", and the FAQ calls it "a persistent volume used
/// for storage, such as container images". Its "Reclaiming Disk Space" guide prunes with the
/// <c>docker</c> command, which is the moby engine's, and offers a factory reset otherwise. The
/// Troubleshooting page offers Reset Kubernetes and Factory Reset, and <c>rdctl reset --factory</c> is
/// the same reset from a command line (<c>src/go/rdctl/cmd/reset.go</c>).</para>
/// </summary>
public sealed record RancherDesktopData : DiskOwner
{
    public static RancherDesktopData Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.Equals("rancher-desktop-data", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => "Rancher Desktop's data";

    public override string Summary(VirtualDisk disk) =>
        "Rancher Desktop's data disk: its container images, volumes and Kubernetes state. It grows as Rancher "
        + "Desktop writes and does not shrink by itself. " + VirtualDiskRoutes.InsideIsNotOnTheDrive;

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "To make it smaller, remove what is no longer needed first. With the moby engine, Rancher Desktop's own "
        + "guide turns Kubernetes off, stops the running containers and then runs 'docker container prune' and "
        + "'docker image prune --all'. Its Troubleshooting page offers Reset Kubernetes, which can remove the "
        + "images too, and Factory Reset ('rdctl reset --factory'), which removes everything."
        + VirtualDiskRoutes.WslCompaction(disk, isSparse);

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Deleting it removes every Rancher Desktop image, volume and Kubernetes workload, and the supported way "
        + "to make it smaller is to prune images and containers, or Rancher Desktop's own Factory Reset.";
}

/// <summary>
/// <c>rancher-desktop</c>, the distribution Rancher Desktop runs its engine in. Rancher Desktop imports it
/// again whenever it is missing and replaces it on every upgrade (<c>ensureDistroRegistered</c> in
/// <c>pkg/rancher-desktop/backend/wsl.ts</c>), so the user's data is never in it and there is nothing to
/// prune.
/// </summary>
public sealed record RancherDesktopSystem : DiskOwner
{
    public static RancherDesktopSystem Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.Equals("rancher-desktop", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => "Rancher Desktop's own Linux distribution";

    public override string Summary(VirtualDisk disk) =>
        "The virtual disk of Rancher Desktop's own Linux distribution, which runs its container engine.";

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "Rancher Desktop makes this distribution itself and replaces it on every upgrade, so there is nothing "
        + "in it to prune.";

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Rancher Desktop needs it to run and makes it itself, so there is nothing here to reclaim.";
}
