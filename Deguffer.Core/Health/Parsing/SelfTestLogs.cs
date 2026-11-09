using System.Buffers.Binary;

namespace Deguffer.Core.Health.Parsing;

/// <summary>How a drive's most recent self-test ended.</summary>
internal enum SelfTestResult
{
    Passed,
    Stopped,
    Failed,
    Running,
}

/// <summary>A drive's most recent self-test.</summary>
/// <param name="PowerOnHours">The drive's hours of use when the test ran.</param>
internal readonly record struct SelfTestOutcome(SelfTestResult Result, ulong PowerOnHours);

/// <summary>
/// The latest entry of each protocol's self-test log. The two share a purpose and no layout, so each
/// has its own reader and both give the same record.
/// </summary>
internal static class SelfTestLogs
{
    private const int AtaEntriesOffset = 2;
    private const int AtaEntryBytes = 24;
    private const int AtaEntryCount = 21;
    private const int AtaStatusOffset = 1;
    private const int AtaHoursOffset = 2;
    private const int AtaIndexOffset = 508;
    private const int AtaInProgress = 0xF;

    private const int NvmeEntriesOffset = 4;
    private const int NvmeEntryBytes = 28;
    private const int NvmeHoursOffset = 4;
    private const int NvmeUnused = 0xF;
    private const int NvmeNeededBytes = NvmeEntriesOffset + NvmeEntryBytes;

    /// <summary>
    /// The latest entry of an ATA self-test log sector, or false where there is none. The log is
    /// circular, and its index names the latest entry counting from 1, with 0 meaning empty.
    /// </summary>
    internal static bool TryReadAta(ReadOnlySpan<byte> sector, out SelfTestOutcome outcome)
    {
        outcome = default;

        if (sector.Length < DriveRequests.SectorBytes)
        {
            return false;
        }

        int index = sector[AtaIndexOffset];

        if (index is 0 or > AtaEntryCount)
        {
            return false;
        }

        var entry = sector.Slice(AtaEntriesOffset + ((index - 1) * AtaEntryBytes), AtaEntryBytes);

        outcome = new SelfTestOutcome(
            (entry[AtaStatusOffset] >> 4) switch
            {
                0 => SelfTestResult.Passed,
                AtaInProgress => SelfTestResult.Running,
                1 or 2 => SelfTestResult.Stopped,
                _ => SelfTestResult.Failed,
            },
            BinaryPrimitives.ReadUInt16LittleEndian(entry[AtaHoursOffset..]));

        return true;
    }

    /// <summary>The newest entry of an NVMe self-test log, which it keeps first, or false where the entry is unused.</summary>
    internal static bool TryReadNvme(ReadOnlySpan<byte> answer, out SelfTestOutcome outcome)
    {
        outcome = default;

        if (!ProtocolData.TryPayload(answer, NvmeNeededBytes, out var page))
        {
            return false;
        }

        var entry = page.Slice(NvmeEntriesOffset, NvmeEntryBytes);
        var status = entry[0] & 0xF;

        if (status == NvmeUnused)
        {
            return false;
        }

        outcome = new SelfTestOutcome(
            status switch
            {
                0 => SelfTestResult.Passed,
                1 or 2 or 3 or 4 or 8 or 9 => SelfTestResult.Stopped,
                _ => SelfTestResult.Failed,
            },
            BinaryPrimitives.ReadUInt64LittleEndian(entry[NvmeHoursOffset..]));

        return true;
    }
}
