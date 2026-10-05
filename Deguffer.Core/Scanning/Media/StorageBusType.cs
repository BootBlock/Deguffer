namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// The <c>STORAGE_BUS_TYPE</c> values that decide a class on their own.
///
/// <para>Deliberately partial, as <see cref="Safety.VolumeFeatures"/> is. SATA, SAS, SCSI, ATA,
/// RAID, Storage Spaces and every value Windows adds later all take the same route, which is the
/// disk's own seek-penalty answer, so naming them would only invite a branch nothing has
/// measured.</para>
/// </summary>
internal enum StorageBusType : byte
{
    Ieee1394 = 0x04,
    Usb = 0x07,
    IScsi = 0x09,
    Sd = 0x0C,
    Mmc = 0x0D,
    Virtual = 0x0E,
    FileBackedVirtual = 0x0F,
    Nvme = 0x11,
}
