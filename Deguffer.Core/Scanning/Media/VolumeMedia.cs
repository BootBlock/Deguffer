namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// What storage one volume sits on.
/// </summary>
/// <param name="Class">The kind of storage, or <see cref="StorageMedia.Unknown"/> with a reason.</param>
/// <param name="PhysicalDisks">
/// The numbers of the physical disks that hold the volume, each once, in ascending order: the
/// <c>N</c> of <c>\\.\PhysicalDriveN</c>. Two volumes that share a number share a disk, so reading
/// them at the same time divides one disk's speed between them. Empty, with a
/// <paramref name="Reason"/>, where the volume did not say, and empty with none for a share or an
/// optical drive, which are not asked.
/// </param>
/// <param name="Reason">
/// Why <paramref name="Class"/> is unknown or <paramref name="PhysicalDisks"/> is not listed, or
/// <see cref="MediaUnknownReason.None"/>.
/// </param>
/// <param name="Win32Error">
/// The error of the call that failed, or 0 where nothing failed or a call succeeded with an answer
/// too short to read.
/// </param>
/// <param name="ThinlyProvisioned">
/// Whether a disk behind the volume says it is thinly provisioned, so the clusters a clean frees go
/// back to the pool, the array or the host behind it only once they are trimmed. False where every
/// disk says it is not, and null where none says it is and one would not answer, or where no disk was
/// asked: a share, an optical drive and a removable volume are not.
/// </param>
public readonly record struct VolumeMedia(
    StorageMedia Class,
    IReadOnlyList<int> PhysicalDisks,
    MediaUnknownReason Reason = MediaUnknownReason.None,
    int Win32Error = 0,
    bool? ThinlyProvisioned = null)
{
    /// <summary>
    /// Whether the volume is on solid state with its disks listed, so more than one reader or walk
    /// may work on it at once. Without its disks a volume cannot be placed with the others on its
    /// disk, and several at once there could be several on one disk from more than one set.
    /// </summary>
    public bool IsKnownSolidState =>
        Class is StorageMedia.Nvme or StorageMedia.SolidState && PhysicalDisks.Count > 0;
}
