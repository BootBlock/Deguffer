using System.Collections.Frozen;
using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>The details every drive can give, each present only where the drive answered.</summary>
internal static class DriveFacts
{
    private static readonly FrozenDictionary<StorageBusType, string> BusNames = new Dictionary<StorageBusType, string>
    {
        [StorageBusType.Scsi] = "SCSI",
        [StorageBusType.Atapi] = "ATAPI",
        [StorageBusType.Ata] = "ATA",
        [StorageBusType.Ieee1394] = "FireWire",
        [StorageBusType.Usb] = "USB",
        [StorageBusType.Raid] = "RAID",
        [StorageBusType.IScsi] = "iSCSI",
        [StorageBusType.Sas] = "SAS",
        [StorageBusType.Sata] = "SATA",
        [StorageBusType.Sd] = "SD card",
        [StorageBusType.Mmc] = "MMC",
        [StorageBusType.Virtual] = "Virtual",
        [StorageBusType.FileBackedVirtual] = "Virtual (file-backed)",
        [StorageBusType.Spaces] = "Storage Spaces",
        [StorageBusType.Nvme] = "NVMe",
    }.ToFrozenDictionary();

    internal static IReadOnlyList<DriveFact> Of(DeviceDescriptor descriptor, IDriveDevice device)
    {
        var facts = new List<DriveFact>();

        Add("Firmware", descriptor.Revision);
        Add("Serial number", descriptor.Serial.Text);
        Add("Connection", Connection(descriptor));

        if (device.Ask(DriveQuestion.SeekPenalty).Bytes is { } seek && StorageDescriptors.TryReadSeekPenalty(seek, out var incurs))
        {
            Add("Type", incurs ? "Hard disk" : "Solid state");
        }

        if (device.Ask(DriveQuestion.Geometry).Bytes is { } geometry && DriveProperties.TryReadDiskSize(geometry, out var size))
        {
            Add("Capacity", FreeSpace.Format(size));
        }

        if (device.Ask(DriveQuestion.Alignment).Bytes is { } alignment && DriveProperties.TryReadSectors(alignment, out var logical, out var physical))
        {
            Add("Sector size", logical == physical ? $"{logical} bytes" : $"{logical} bytes, {physical} bytes physical");
        }

        if (device.Ask(DriveQuestion.Trim).Bytes is { } trim && DriveProperties.TryReadTrim(trim, out var enabled))
        {
            Add("TRIM", enabled ? "On" : "Off");
        }

        return facts;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                facts.Add(new DriveFact(label, value));
            }
        }
    }

    private static string Connection(DeviceDescriptor descriptor)
    {
        var bus = BusNames.GetValueOrDefault(descriptor.Bus, "Unknown");

        return descriptor.IsRemovable ? $"{bus} (removable)" : bus;
    }
}
