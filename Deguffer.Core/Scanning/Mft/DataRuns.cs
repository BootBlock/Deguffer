namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One contiguous span of clusters belonging to a non-resident attribute.
/// </summary>
/// <param name="IsSparse">
/// A run that occupies virtual clusters but no physical ones, and reads back as zeroes. It has to
/// be represented rather than dropped: virtual cluster numbers are positional, so removing a run
/// from the list shifts every run after it onto the wrong part of the disk.
/// </param>
public readonly record struct DataRun(long StartCluster, long ClusterCount, bool IsSparse = false);

/// <summary>
/// Decodes an NTFS mapping pair list — the compact, delta-encoded form in which a non-resident
/// attribute records where its clusters live.
///
/// Needed for exactly one attribute: <c>$MFT</c>'s own <c>$DATA</c>. The MFT is not necessarily
/// contiguous, and a reader that assumes it is will read the correct number of bytes from the wrong
/// place once the table has grown — producing records that parse cleanly and describe nothing.
/// </summary>
public static class DataRuns
{
    /// <summary>
    /// Decode until the terminating zero byte. Sparse runs are returned flagged rather than
    /// omitted — they hold no data, but they do hold a position, and a caller translating a virtual
    /// cluster has to count them to land anywhere near the right place.
    /// </summary>
    public static IReadOnlyList<DataRun> Parse(ReadOnlySpan<byte> mappingPairs)
    {
        var runs = new List<DataRun>();
        long cluster = 0;
        var offset = 0;

        while (offset < mappingPairs.Length && mappingPairs[offset] != 0)
        {
            var header = mappingPairs[offset++];
            int lengthSize = header & 0x0F;
            int offsetSize = (header >> 4) & 0x0F;

            if (lengthSize == 0 || offset + lengthSize + offsetSize > mappingPairs.Length)
            {
                break;
            }

            var count = ReadUnsigned(mappingPairs.Slice(offset, lengthSize));
            offset += lengthSize;

            if (count <= 0)
            {
                break;
            }

            if (offsetSize == 0)
            {
                // A sparse run has a length but no location. Record it so it consumes its share of
                // the virtual cluster space; the previous run's start is unchanged, since there is
                // no delta to apply.
                runs.Add(new DataRun(StartCluster: 0, count, IsSparse: true));
                continue;
            }

            // The offset is a *signed delta* from the previous run's start, not an absolute
            // cluster. Treating it as absolute happens to work for the first run and then fails.
            cluster += ReadSigned(mappingPairs.Slice(offset, offsetSize));
            offset += offsetSize;

            if (cluster < 0)
            {
                break;
            }

            runs.Add(new DataRun(cluster, count));
        }

        return runs;
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;

        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[^1];

        for (var i = bytes.Length - 2; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
