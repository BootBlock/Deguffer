namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// Why a volume's media is <see cref="StorageMedia.Unknown"/>. Each value names the step that did
/// not answer, so a report of an unknown drive says where to look rather than only that it failed.
/// </summary>
public enum MediaUnknownReason
{
    /// <summary>The class is known.</summary>
    None,

    /// <summary>
    /// <c>GetDriveType</c> gave no kind for the mount point, so there is no volume there to ask.
    /// </summary>
    KindNotReported,

    /// <summary>
    /// The volume did not say which disks hold it: the volume would not open, it is not on a disk
    /// at all (a <c>subst</c> letter), or the answer was malformed.
    /// </summary>
    ExtentsNotRead,

    /// <summary>The volume answered, and named no disk.</summary>
    NoDiskBehindVolume,

    /// <summary>A disk behind the volume would not open, or did not say what bus it is on.</summary>
    AdapterNotRead,

    /// <summary>
    /// A disk whose bus does not decide the class did not say whether it incurs a seek penalty.
    /// A Storage Spaces disk that gives no answer ends here, as the bus alone says nothing about
    /// the disks in its pool.
    /// </summary>
    SeekPenaltyNotRead,

    /// <summary>
    /// The disks behind a spanned or striped volume are of kinds with no order of speed between
    /// them, such as a USB disk and a virtual disk, so no one of them is the slowest.
    /// </summary>
    DisksOfUnrankedKinds,
}
