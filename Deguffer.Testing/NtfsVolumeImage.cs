using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// Lays a table of records out on a volume image: a boot sector at the start, then the table in
/// one extent or two, with record 0 rewritten as <c>$MFT</c>'s own record saying where they are.
///
/// <para>The table ends where its last record does, as a real table's data size does, rather than
/// being padded to a cluster. Where records are smaller than sectors, that can be part-way through
/// a sector, and a reader that only reads whole sectors of records has the rest to deal with.</para>
/// </summary>
internal static class NtfsVolumeImage
{
    /// <summary>Clusters left unused between the two extents of a split table.</summary>
    private const int GapClusters = 3;

    public static SectorStrictVolume Build(
        IReadOnlyList<byte[]> records, int bytesPerRecord, int bytesPerSector, int bytesPerCluster, long? gapAfterCluster)
    {
        // Past the boot sector, and past the largest sector whatever the cluster size.
        var mftStart = Math.Max(1, 2 * NtfsBootSector.MaximumBytesPerSector / bytesPerCluster);
        var tableBytes = (long)records.Count * bytesPerRecord;
        var tableClusters = (tableBytes + bytesPerCluster - 1) / bytesPerCluster;

        IReadOnlyList<DataRun> runs = gapAfterCluster is { } split
            ? [new DataRun(mftStart, split), new DataRun(mftStart + split + GapClusters, tableClusters - split)]
            : [new DataRun(mftStart, tableClusters)];

        var table = new byte[tableClusters * bytesPerCluster];

        for (var i = 0; i < records.Count; i++)
        {
            records[i].CopyTo(table, (long)i * bytesPerRecord);
        }

        MftRecordBytes.SelfRecord(runs, tableBytes, bytesPerRecord: bytesPerRecord).CopyTo(table, 0);

        var end = runs.Max(run => run.StartCluster + run.ClusterCount);
        var image = new byte[end * bytesPerCluster];

        NtfsBootSectorBytes.Build(
                (ushort)bytesPerSector,
                (byte)(bytesPerCluster / bytesPerSector),
                mftStart,
                NtfsBootSectorBytes.RecordSizeCode(bytesPerRecord, bytesPerCluster))
            .CopyTo(image, 0);

        long virtualCluster = 0;

        foreach (var run in runs)
        {
            Array.Copy(
                table,
                virtualCluster * bytesPerCluster,
                image,
                run.StartCluster * bytesPerCluster,
                run.ClusterCount * bytesPerCluster);

            virtualCluster += run.ClusterCount;
        }

        return new SectorStrictVolume(image, bytesPerSector);
    }
}
