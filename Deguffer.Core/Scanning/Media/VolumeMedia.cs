namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// What storage one volume sits on.
/// </summary>
/// <param name="Class">The kind of storage, or <see cref="StorageMedia.Unknown"/> with a reason.</param>
/// <param name="PhysicalDisks">
/// The numbers of the physical disks that hold the volume, each once, in ascending order: the
/// <c>N</c> of <c>\\.\PhysicalDriveN</c>. Two volumes that share a number share a disk, so reading
/// them at the same time divides one disk's speed between them. Empty where the volume did not say,
/// and always empty for a share.
/// </param>
/// <param name="Reason">Why <paramref name="Class"/> is unknown, or <see cref="MediaUnknownReason.None"/>.</param>
/// <param name="Win32Error">
/// The error of the call that failed, or 0 where nothing failed or a call succeeded with an answer
/// too short to read.
/// </param>
public readonly record struct VolumeMedia(
    StorageMedia Class,
    IReadOnlyList<int> PhysicalDisks,
    MediaUnknownReason Reason = MediaUnknownReason.None,
    int Win32Error = 0);
