namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// The kind of storage a volume sits on, in the terms that decide how it should be read.
///
/// <para><see cref="System.IO.DriveType"/> cannot answer this: it reports
/// <see cref="System.IO.DriveType.Fixed"/> for an NVMe drive, a SATA SSD and a spinning disk alike,
/// and those three want opposite things from a scan. Concurrent random reads slow a spinning disk
/// down, and an NVMe drive needs many of them in flight to reach its speed.</para>
/// </summary>
public enum StorageMedia
{
    /// <summary>
    /// Windows did not say, or said something that does not fit one class. A reader of this value
    /// treats the volume conservatively. <see cref="VolumeMedia.Reason"/> says why.
    /// </summary>
    Unknown,

    /// <summary>Solid state on the NVMe bus.</summary>
    Nvme,

    /// <summary>Solid state on any other bus: SATA, SAS, a RAID adapter, a Storage Spaces disk.</summary>
    SolidState,

    /// <summary>A disk that reports a seek penalty, which in practice means one that spins.</summary>
    Rotational,

    /// <summary>
    /// Removable media, or a disk attached over USB, SD, MMC or FireWire. What is behind the bridge
    /// is not reported reliably, so a USB enclosure holding an NVMe drive is classed here too.
    /// </summary>
    Removable,

    /// <summary>A share, or a disk reached over iSCSI: every read is a network round trip.</summary>
    Network,

    /// <summary>
    /// A disk that is a file or a construct of a driver, such as a mounted VHD. The disk that holds
    /// the file is not known from here, so its speed is not either.
    /// </summary>
    Virtual,
}
