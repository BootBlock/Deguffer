using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>The parts of NVMe Identify Controller the page uses.</summary>
/// <param name="Version">The NVMe version the controller implements, as a major and a minor number.</param>
/// <param name="WarningKelvin">The composite temperature the controller warns at, or 0 where it reports none.</param>
/// <param name="CriticalKelvin">The composite temperature it treats as critical, or 0 where it reports none.</param>
internal readonly record struct NvmeController((int Major, int Minor) Version, int WarningKelvin, int CriticalKelvin)
{
    private const int VersionOffset = 80;
    private const int WarningOffset = 266;
    private const int CriticalOffset = 268;
    private const int NeededBytes = CriticalOffset + sizeof(ushort);

    internal static bool TryRead(ReadOnlySpan<byte> answer, out NvmeController controller)
    {
        controller = default;

        if (!ProtocolData.TryPayload(answer, NeededBytes, out var page))
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(page[VersionOffset..]);

        controller = new NvmeController(
            ((int)(version >> 16), (int)((version >> 8) & 0xFF)),
            BinaryPrimitives.ReadUInt16LittleEndian(page[WarningOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(page[CriticalOffset..]));

        return true;
    }
}
