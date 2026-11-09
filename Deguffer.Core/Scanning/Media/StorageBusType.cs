namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// The <c>STORAGE_BUS_TYPE</c> values something in Deguffer decides on: a class on their own, how a
/// drive's health can be read, or the name the Health page gives a bus.
///
/// <para>Deliberately partial, as <see cref="Safety.VolumeFeatures"/> is. A value with no name here
/// takes the route every unknown bus takes, so naming the rest would only invite a branch nothing has
/// measured.</para>
/// </summary>
internal enum StorageBusType : byte
{
    Scsi = 0x01,
    Atapi = 0x02,
    Ata = 0x03,
    Ieee1394 = 0x04,
    Usb = 0x07,
    Raid = 0x08,
    IScsi = 0x09,
    Sas = 0x0A,
    Sata = 0x0B,
    Sd = 0x0C,
    Mmc = 0x0D,
    Virtual = 0x0E,
    FileBackedVirtual = 0x0F,
    Spaces = 0x10,
    Nvme = 0x11,
}
