namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The disk Docker Desktop keeps its images, containers, volumes and build cache in. Docker's own
/// accounting can say what is reclaimable inside it, and Docker's own commands are the route.
///
/// <para>Three files have been this disk: <c>docker_data.vhdx</c> from Docker Desktop 4.30, the
/// <c>docker-desktop-data</c> distribution an older installation keeps using after an upgrade, and
/// <c>DockerDesktop.vhdx</c> under the Hyper-V backend. Only the second is a WSL distribution, so only
/// that one is claimed by name, and <see cref="DockerDesktopDisks"/> finds the other two.</para>
/// </summary>
public sealed record DockerDesktopData : DiskOwner
{
    public static DockerDesktopData Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => "Docker Desktop's data";

    public override string Summary(VirtualDisk disk) =>
        "Docker Desktop's data disk: every image, container, volume and build cache Docker keeps. It grows as "
        + "Docker writes and does not shrink by itself. " + VirtualDiskRoutes.InsideIsNotOnTheDrive;

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "To make it smaller, remove what Docker no longer needs first, with 'docker system prune' and "
        + "'docker builder prune'. Unused volumes hold data, and go only when '--volumes' is added. Docker "
        + "Desktop 4.34 and later give freed space back to the drive by themselves, which 4.59 turned off for "
        + "the time being. Docker Desktop's Troubleshoot page offers 'Clean up data', which resets the disk "
        + "and removes every container and image.";

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Deleting it removes every Docker image, container and volume, and the supported way to make it "
        + "smaller is 'docker system prune', while Docker Desktop's own 'Clean up data' empties it entirely.";
}

/// <summary>
/// The <c>docker-desktop</c> distribution, which is Docker Desktop's own small Linux rather than the
/// user's data. Docker Desktop makes it again, so there is nothing in it to prune.
/// </summary>
public sealed record DockerDesktopSystem : DiskOwner
{
    public static DockerDesktopSystem Instance { get; } = new();

    public override bool ClaimsDistribution(string name) =>
        name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase);

    public override string Name(VirtualDisk disk) => "Docker Desktop's own Linux distribution";

    public override string Summary(VirtualDisk disk) =>
        "The virtual disk of Docker Desktop's own Linux distribution, which runs the Docker engine.";

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "Docker Desktop makes this distribution itself and keeps it small, so there is nothing in it to prune.";

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Docker Desktop needs it to run and makes it itself, so there is nothing here to reclaim.";
}
