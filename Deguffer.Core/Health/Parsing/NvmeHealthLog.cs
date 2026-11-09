using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The NVMe SMART / Health Information log, page 2.</summary>
internal sealed record NvmeHealthLog(
    byte CriticalWarning,
    int TemperatureKelvin,
    byte AvailableSpare,
    byte SpareThreshold,
    byte PercentageUsed,
    UInt128 DataUnitsRead,
    UInt128 DataUnitsWritten,
    UInt128 HostReads,
    UInt128 HostWrites,
    UInt128 BusyMinutes,
    UInt128 PowerCycles,
    UInt128 PowerOnHours,
    UInt128 UnsafeShutdowns,
    UInt128 MediaErrors,
    UInt128 ErrorLogEntries,
    uint WarningTemperatureMinutes,
    uint CriticalTemperatureMinutes,
    IReadOnlyList<int> SensorKelvin)
{
    /// <summary>The fields read here end with the eighth temperature sensor.</summary>
    private const int NeededBytes = 216;

    private const int SensorOffset = 200;
    private const int SensorCount = 8;

    internal static bool TryRead(ReadOnlySpan<byte> answer, out NvmeHealthLog? log)
    {
        log = null;

        if (!ProtocolData.TryPayload(answer, NeededBytes, out var page))
        {
            return false;
        }

        var sensors = new List<int>(SensorCount);

        for (var i = 0; i < SensorCount; i++)
        {
            // Zero marks a sensor the controller does not have.
            if (BinaryPrimitives.ReadUInt16LittleEndian(page[(SensorOffset + (i * sizeof(ushort)))..]) is var kelvin and > 0)
            {
                sensors.Add(kelvin);
            }
        }

        log = new NvmeHealthLog(
            page[0],
            BinaryPrimitives.ReadUInt16LittleEndian(page[1..]),
            page[3],
            page[4],
            page[5],
            Counter(page, 32),
            Counter(page, 48),
            Counter(page, 64),
            Counter(page, 80),
            Counter(page, 96),
            Counter(page, 112),
            Counter(page, 128),
            Counter(page, 144),
            Counter(page, 160),
            Counter(page, 176),
            BinaryPrimitives.ReadUInt32LittleEndian(page[192..]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[196..]),
            sensors);

        return true;
    }

    private static UInt128 Counter(ReadOnlySpan<byte> page, int offset) =>
        BinaryPrimitives.ReadUInt128LittleEndian(page[offset..]);
}
