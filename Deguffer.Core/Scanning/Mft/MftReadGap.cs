namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The longest run of records nobody wants that one read of the table reads across rather than
/// ending there and making a second read after it. Both passes end a read at a longer run: the
/// first at free records (<see cref="MftBatchPlanner"/>), and the second at records it does not
/// want (<see cref="MftExtensionReader"/>).
///
/// <para>Sixty-four kibibytes, sixty-four of the usual 1,024-byte records, is about what a SATA
/// drive transfers in the time a second read costs it. Reading across a shorter run saves that
/// read. Reading across a longer one only brings records nobody parses: a 16 MiB read across free
/// records costs some thirty milliseconds at 550 MB/s, against about a tenth of one for the second
/// read. NVMe makes a second read cheaper still, so a shorter run would break even there and this
/// threshold reads across some runs two reads would serve faster. The most such a run costs is the
/// transfer of sixty-four kibibytes, some twenty microseconds at 3 GB/s.</para>
/// </summary>
internal static class MftReadGap
{
    public const int Bytes = 64 * 1024;

    /// <summary>
    /// How many records long a run of unwanted records can be and still be read across. Never
    /// zero, so a table of records larger than <see cref="Bytes"/> still reads across one.
    /// </summary>
    public static int Records(int bytesPerRecord) => Math.Max(1, Bytes / bytesPerRecord);
}
