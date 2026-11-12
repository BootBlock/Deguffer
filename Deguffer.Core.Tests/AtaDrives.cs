using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>A SATA drive as Windows describes it, with an invented model, firmware and serial.</summary>
internal static class AtaDrives
{
    internal static DeviceDescriptor Named(string model, string firmware = "TEST01") =>
        new(StorageBusType.Sata, IsRemovable: false, Vendor: string.Empty, model, firmware, new DriveSerial(DriveBytes.InventedSerial));
}
