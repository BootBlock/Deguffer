using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>One ATA SMART attribute as the drive holds it.</summary>
/// <param name="Threshold">The drive's failure threshold for it, or null where the page could not read one.</param>
internal readonly record struct AtaAttribute(byte Id, bool IsPreFail, byte Value, byte Worst, ulong Raw, byte? Threshold);

/// <summary>Reads the ATA SMART attribute and threshold sectors, which share one layout.</summary>
internal static class AtaAttributeTable
{
    /// <summary>A two-byte revision, then thirty 12-byte entries.</summary>
    private const int HeaderBytes = 2;

    private const int EntryBytes = 12;
    private const int EntryCount = 30;
    private const int TableBytes = HeaderBytes + (EntryCount * EntryBytes);

    private const int FlagsOffset = 1;
    private const int ValueOffset = 3;
    private const int WorstOffset = 4;
    private const int RawOffset = 5;
    private const int RawBytes = 6;
    private const int ThresholdOffset = 1;
    private const int PreFailFlag = 1;

    /// <summary>
    /// The attributes in an answer to <c>IOCTL_STORAGE_PREDICT_FAILURE</c>, with whether the drive
    /// predicts its own failure.
    /// </summary>
    internal static bool TryReadPrediction(ReadOnlySpan<byte> answer, out bool predictsFailure, out IReadOnlyList<AtaAttribute> attributes)
    {
        predictsFailure = false;
        attributes = [];

        if (answer.Length < sizeof(uint) + TableBytes)
        {
            return false;
        }

        predictsFailure = BinaryPrimitives.ReadUInt32LittleEndian(answer) != 0;
        attributes = Attributes(answer[sizeof(uint)..]);
        return true;
    }

    /// <summary>Each threshold in a thresholds sector, by attribute. The first of a repeated ID stands.</summary>
    internal static bool TryReadThresholds(ReadOnlySpan<byte> sector, out IReadOnlyDictionary<byte, byte> thresholds)
    {
        var found = new Dictionary<byte, byte>();
        thresholds = found;

        if (sector.Length < TableBytes)
        {
            return false;
        }

        for (var at = HeaderBytes; at < TableBytes; at += EntryBytes)
        {
            if (sector[at] != 0)
            {
                found.TryAdd(sector[at], sector[at + ThresholdOffset]);
            }
        }

        return true;
    }

    /// <summary><paramref name="attributes"/> with the threshold of each that has one.</summary>
    internal static IReadOnlyList<AtaAttribute> WithThresholds(IReadOnlyList<AtaAttribute> attributes, IReadOnlyDictionary<byte, byte> thresholds) =>
        [.. attributes.Select(attribute => thresholds.TryGetValue(attribute.Id, out var threshold)
            ? attribute with { Threshold = threshold }
            : attribute)];

    private static List<AtaAttribute> Attributes(ReadOnlySpan<byte> table)
    {
        var attributes = new List<AtaAttribute>();
        var seen = new HashSet<byte>();

        for (var at = HeaderBytes; at < TableBytes; at += EntryBytes)
        {
            var entry = table.Slice(at, EntryBytes);

            // A row is keyed by its ID, so a repeat would be two rows claiming one place.
            if (entry[0] != 0 && seen.Add(entry[0]))
            {
                attributes.Add(new AtaAttribute(
                    entry[0],
                    (BinaryPrimitives.ReadUInt16LittleEndian(entry[FlagsOffset..]) & PreFailFlag) != 0,
                    entry[ValueOffset],
                    entry[WorstOffset],
                    Raw(entry.Slice(RawOffset, RawBytes)),
                    Threshold: null));
            }
        }

        return attributes;
    }

    private static ulong Raw(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;

        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
