namespace Deguffer.Core.Safety;

/// <summary>
/// The questions <see cref="VolumeInventory"/> asks the machine to build its list, and the two
/// mount-manager calls it answers <see cref="IVolumeInventory.MountPointsOf"/> with, so a test can
/// present a volume of any kind and see which of them were asked about it.
///
/// <para>Narrower than <see cref="VolumeCalls"/>, which also answers the other questions
/// <see cref="IVolumeInventory"/> asks live: those take a path from the caller, and a fake inventory
/// already stands in for them. What a fake inventory cannot show is what reading the list costs, and
/// that cost is a defect where it waits on a share no caller wants anything of (#181). Nor can it
/// show what the inventory's own mount-manager calls cost a pass, which
/// <see cref="Diagnostics.MachineCallTally"/> counts where they are asked here (#516).</para>
/// </summary>
internal interface IVolumeQueries
{
    /// <summary>Every volume of this machine, by its <c>\\?\Volume{GUID}\</c> name. See <see cref="VolumeCalls.Names"/>.</summary>
    IEnumerable<string> Names();

    /// <summary>
    /// The <c>\\?\Volume{GUID}\</c> name of the volume mounted at <paramref name="mountPoint"/>, or null
    /// where Windows will not say. See <see cref="VolumeCalls.VolumeNameOf"/>.
    /// </summary>
    /// <param name="mountPoint">A mount point with its trailing separator, which is the only form Windows answers for.</param>
    string? VolumeNameOf(string mountPoint);

    /// <summary>Every place the volume named <paramref name="volumeName"/> is mounted. See <see cref="VolumeCalls.MountPointsOf"/>.</summary>
    IReadOnlyList<string> MountPointsOf(string volumeName);

    /// <summary>The root of every drive letter in use, a mapped share's included.</summary>
    /// <exception cref="IOException">Windows would not list them.</exception>
    IReadOnlyList<string> Letters();

    /// <summary>Fixed, removable, network and so on. See <see cref="VolumeCalls.KindOf"/>.</summary>
    DriveType KindOf(string mountPoint);

    /// <summary>
    /// Whether the root at <paramref name="mountPoint"/> can be read. Waits on the device, and for a
    /// share on the network, for as long as either takes to answer.
    /// </summary>
    PathPresence Probe(string mountPoint);

    /// <summary>The label, the flags and the file system's name. See <see cref="VolumeCalls.InformationOf"/>.</summary>
    (string? Label, VolumeFeatures? Features, string? FileSystem) InformationOf(string mountPoint);

    /// <summary>Capacity and what is left of it for this user. See <see cref="VolumeCalls.SpaceOf"/>.</summary>
    (long Total, long Free)? SpaceOf(string mountPoint);
}
