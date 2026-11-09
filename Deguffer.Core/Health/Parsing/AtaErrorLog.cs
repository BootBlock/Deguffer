using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The summary SMART error log, address <c>0x01</c>.</summary>
internal static class AtaErrorLog
{
    private const int DeviceErrorCountOffset = 452;

    /// <summary>How many errors the drive has counted over its life.</summary>
    internal static bool TryReadCount(ReadOnlySpan<byte> sector, out int count)
    {
        count = 0;

        if (sector.Length < DriveRequests.SectorBytes)
        {
            return false;
        }

        count = BinaryPrimitives.ReadUInt16LittleEndian(sector[DeviceErrorCountOffset..]);
        return true;
    }
}
