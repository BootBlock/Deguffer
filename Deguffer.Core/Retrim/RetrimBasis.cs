namespace Deguffer.Core.Retrim;

/// <summary>
/// Why a volume is offered a retrim, which decides what the offer says a retrim gives back.
/// </summary>
public enum RetrimBasis
{
    /// <summary>
    /// A disk behind the volume is thinly provisioned: a Storage Spaces space, a disk on a storage
    /// array, or a virtual machine's disk. The pool, the array or the host takes the space back only
    /// once the volume is trimmed.
    /// </summary>
    ThinlyProvisioned,

    /// <summary>
    /// The volume is in a virtual disk this machine mounted. A dynamic or differencing disk gives the
    /// space back to the drive that holds its file only once the volume is trimmed.
    /// </summary>
    VirtualDisk,

    /// <summary>
    /// The volume is on solid state, which learns which of its blocks are free only when Windows
    /// trims the volume. Nothing goes back to another drive.
    /// </summary>
    SolidState,
}
