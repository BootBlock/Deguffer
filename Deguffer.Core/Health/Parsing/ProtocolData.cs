using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>Finds the payload of a protocol-specific answer from the place the driver says it put it.</summary>
internal static class ProtocolData
{
    /// <summary>
    /// <c>STORAGE_PROTOCOL_DATA_DESCRIPTOR</c>: version, size, then the protocol data, whose offset and
    /// length are counted from that structure.
    /// </summary>
    private const int ProtocolStart = 8;

    private const int OffsetField = ProtocolStart + 16;
    private const int LengthField = ProtocolStart + 20;

    /// <summary>False where the declared place lies outside the answer, or holds less than <paramref name="needed"/>.</summary>
    internal static bool TryPayload(ReadOnlySpan<byte> answer, int needed, out ReadOnlySpan<byte> payload)
    {
        payload = default;

        if (answer.Length < LengthField + sizeof(int))
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(answer[OffsetField..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(answer[LengthField..]);
        var start = ProtocolStart + (long)offset;

        if (length < needed || start + length > answer.Length)
        {
            return false;
        }

        payload = answer.Slice((int)start, (int)length);
        return true;
    }
}
