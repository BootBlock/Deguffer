namespace Deguffer.Benchmark;

/// <summary>
/// How many records of a volume's file table are in use, counted once before any run is timed.
///
/// <para>A pass never parses a free record, but it can read one with the records around it. What a
/// run read beyond the records in use is what it read for nothing, and the bitmap is the only place
/// that says how many are in use. Counted once rather than in every run, so no run is timed reading
/// the bitmap twice. The volume is live, so a run can see a few records more or fewer than this.
/// </para>
/// </summary>
/// <param name="RecordsInUse">How many records <c>$MFT</c>'s <c>$BITMAP</c> marks in use.</param>
internal readonly record struct TableCensus(long RecordsInUse, int BytesPerRecord)
{
    /// <summary>
    /// The bytes of free records a run read, or nothing where it read fewer records than are in use.
    /// Only a complete run's figure is exact: a run that stopped short never read some records in
    /// use, so its figure is less than what it read of free records.
    /// </summary>
    public long FreeBytesRead(RunTally tally) => Math.Max(0, tally.Items - RecordsInUse) * BytesPerRecord;
}
