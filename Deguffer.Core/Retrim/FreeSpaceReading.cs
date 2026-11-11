using Deguffer.Core.Safety;

namespace Deguffer.Core.Retrim;

/// <summary>
/// The free space of every fixed volume, read now, for <see cref="RetrimOffer.For"/> to compare across
/// a clean.
///
/// <para>Read through <see cref="IVolumeInventory.SpaceOf"/> rather than taken from
/// <see cref="IVolumeInventory.Volumes"/>, whose figures are remembered from when the list was last read
/// and would show a clean as having freed nothing. Only the volumes a retrim can be offered on are
/// read, since each reading asks the volume.</para>
/// </summary>
public static class FreeSpaceReading
{
    /// <summary>
    /// The fixed, ready volumes Windows names, each with its free space as it is now, or with none
    /// where the volume would not say.
    /// </summary>
    public static IReadOnlyList<LocalVolume> Take(IVolumeInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return
        [
            .. inventory.Volumes
                .Where(volume => volume is { Kind: DriveType.Fixed, Readiness: VolumeReadiness.Ready, VolumeName: not null })
                .Select(volume => volume with { FreeBytes = inventory.SpaceOf(volume.RootPath)?.Free }),
        ];
    }
}
