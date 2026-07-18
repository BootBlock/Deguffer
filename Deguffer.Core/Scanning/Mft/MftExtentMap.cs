using System.Buffers.Binary;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Where the MFT's own records physically live, read from record 0 — the entry <c>$MFT</c> keeps
/// about itself.
///
/// The table is not necessarily contiguous. A reader that assumes it is will, on any volume whose
/// MFT has ever grown, read the right number of bytes from the wrong place and produce records that
/// parse cleanly and describe nothing.
/// </summary>
public sealed record MftExtentMap(long DataSize, IReadOnlyList<DataRun> Runs)
{
    private const uint AttributeList = 0x20;

    /// <summary>
    /// Read the extent map out of <paramref name="record0"/>, which is modified in place by the
    /// update sequence fixup.
    /// </summary>
    public static bool TryRead(Span<byte> record0, int bytesPerSector, out MftExtentMap map)
    {
        map = default!;

        if (!MftRecordHeader.TryRead(record0, bytesPerSector, out var header))
        {
            return false;
        }

        var attributes = new MftAttributeEnumerator(record0[..header.UsedLength], header.FirstAttributeOffset);

        while (attributes.MoveNext())
        {
            // On a heavily fragmented volume $MFT's run list outgrows one record and spills into
            // extension records reached through an $ATTRIBUTE_LIST. Following that chain is not
            // implemented, and reading only the runs that fit here would index part of the volume
            // and report short sizes for everything outside it — a wrong number, silently. Refuse
            // instead, and let the caller take the slow route.
            if (attributes.CurrentType == AttributeList)
            {
                return false;
            }

            // $MFT's data is always non-resident and always the unnamed stream — it is the largest
            // thing on the volume, so a resident one would mean this is not record 0 at all.
            if (attributes.CurrentType != MftRecordParser.AttributeData
                || attributes.Current[0x08] == 0
                || attributes.Current[0x09] != 0)
            {
                continue;
            }

            return TryReadRuns(attributes.Current, out map);
        }

        return false;
    }

    private static bool TryReadRuns(ReadOnlySpan<byte> attribute, out MftExtentMap map)
    {
        map = default!;

        if (attribute.Length < 0x40)
        {
            return false;
        }

        int mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        var dataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);

        if (mappingPairsOffset >= attribute.Length || dataSize <= 0)
        {
            return false;
        }

        var runs = DataRuns.Parse(attribute[mappingPairsOffset..]);
        if (runs.Count == 0)
        {
            return false;
        }

        map = new MftExtentMap(dataSize, runs);
        return true;
    }

    /// <summary>
    /// Translate a virtual cluster within the MFT stream to its physical cluster, and report how
    /// many clusters remain contiguous from there.
    ///
    /// The contiguous count is what lets the caller read in large batches without straddling an
    /// extent boundary — reading across one would splice unrelated regions of the disk into what
    /// looks like a run of consecutive records.
    /// </summary>
    public bool TryTranslate(long virtualCluster, out long physicalCluster, out long contiguousClusters)
    {
        physicalCluster = 0;
        contiguousClusters = 0;

        long seen = 0;

        foreach (var run in Runs)
        {
            if (virtualCluster < seen + run.ClusterCount)
            {
                // A sparse run occupies this position but holds nothing to read. Refusing is the
                // only honest answer: reporting the next run's clusters here would return real data
                // from the wrong offset.
                if (run.IsSparse)
                {
                    return false;
                }

                var into = virtualCluster - seen;
                physicalCluster = run.StartCluster + into;
                contiguousClusters = run.ClusterCount - into;
                return true;
            }

            // Sparse runs are counted here as well as elsewhere: virtual cluster numbers are
            // positional, so skipping one would shift every later run onto the wrong disk offset.
            seen += run.ClusterCount;
        }

        return false;
    }
}
