namespace Deguffer.Core.Safety;

/// <summary>The questions <see cref="IVolumeQueries"/> names, asked of this machine.</summary>
internal sealed class VolumeQueries : IVolumeQueries
{
    /// <summary>The one instance there is any use for (G5): nothing here holds state.</summary>
    public static readonly VolumeQueries Machine = new();

    private VolumeQueries()
    {
    }

    public IEnumerable<string> Names() => VolumeCalls.Names();

    public IReadOnlyList<string> MountPointsOf(string volumeName) => VolumeCalls.MountPointsOf(volumeName);

    public IReadOnlyList<string> Letters() => Environment.GetLogicalDrives();

    public DriveType KindOf(string mountPoint) => VolumeCalls.KindOf(mountPoint);

    public PathPresence Probe(string mountPoint) => LongPath.ProbeDirectory(mountPoint);

    public (string? Label, VolumeFeatures? Features, string? FileSystem) InformationOf(string mountPoint) =>
        VolumeCalls.InformationOf(mountPoint);

    public (long Total, long Free)? SpaceOf(string mountPoint) => VolumeCalls.SpaceOf(mountPoint);
}
