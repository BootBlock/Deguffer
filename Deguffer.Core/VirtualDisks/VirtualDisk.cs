namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Whose virtual disk a <see cref="VirtualDisk"/> is, which decides what is inside it and the route
/// that makes it smaller.
/// </summary>
public enum VirtualDiskKind
{
    /// <summary>
    /// A WSL distribution the user installed or imported: its whole Linux file system. WSL's own
    /// commands are the route.
    /// </summary>
    WslDistribution,

    /// <summary>
    /// The disk Docker Desktop keeps its images, containers, volumes and build cache in. Docker's own
    /// accounting can say what is reclaimable inside it, and Docker's own commands are the route.
    ///
    /// <para>Three files have been this disk: <c>docker_data.vhdx</c> from Docker Desktop 4.30, the
    /// <c>docker-desktop-data</c> distribution an older installation keeps using after an upgrade, and
    /// <c>DockerDesktop.vhdx</c> under the Hyper-V backend.</para>
    /// </summary>
    DockerData,

    /// <summary>
    /// The <c>docker-desktop</c> distribution, which is Docker Desktop's own small Linux rather than
    /// the user's data. Docker Desktop makes it again, so there is nothing in it to prune.
    /// </summary>
    DockerSystem,
}

/// <summary>
/// One virtual disk a tool on this machine has registered or configured, found from that tool's own
/// records and never by searching for <c>.vhdx</c> files.
///
/// <para>A file with the right extension is not evidence of anything: a disk somebody copied aside, a
/// Hyper-V machine's disk and a WSL distribution's disk look alike, and only the tool's own record
/// says which tool would be harmed and which route applies.</para>
/// </summary>
/// <param name="Path">The disk file, in display form, whether or not it is there.</param>
/// <param name="Kind">Whose disk it is.</param>
/// <param name="Distribution">
/// The WSL distribution the disk belongs to, as WSL names it, or null for a disk no distribution owns,
/// which is Docker's own data disk from 4.30 and its Hyper-V disk.
/// </param>
public sealed record VirtualDisk(string Path, VirtualDiskKind Kind, string? Distribution)
{
    /// <summary>The name a reader knows the disk by: the distribution, or Docker Desktop.</summary>
    public string Owner => Kind switch
    {
        VirtualDiskKind.WslDistribution => $"the WSL distribution '{Distribution}'",
        VirtualDiskKind.DockerSystem => "Docker Desktop's own Linux distribution",
        _ => "Docker Desktop's data",
    };
}
