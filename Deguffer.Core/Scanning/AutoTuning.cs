using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What Auto means for each kind of storage, and the measurement each value comes from.
///
/// <para><b>A kind nobody has measured gets the conservative value</b>, which is the value every
/// scan used before these could be set. It is not known to be right for that kind, only known not to
/// be a regression on it. A value changes here when a measurement on that kind says it should, and
/// the measurement goes beside it.</para>
///
/// <para>Every figure below is from <c>Deguffer.Benchmark</c> on one machine with 32 logical
/// processors, two NVMe drives and one SATA SSD, while other work was running. No spinning, removable,
/// network or virtual disk was available, so those kinds keep the conservative values.</para>
/// </summary>
internal static class AutoTuning
{
    /// <summary>
    /// The conservative <c>min(2 × processors, 16)</c> on every kind.
    ///
    /// <para>On NVMe it is also what was measured. The walk was timed at 4, 8, 12, 16, 24 and 32
    /// threads, two rounds of five runs, over <c>C:\Windows\WinSxS</c> (238,031 entries),
    /// <c>C:\Program Files</c> (186,697) and a source tree (1.24 M). 4 threads took 1.7 to 2.4 times
    /// as long as 16, and 8 lost on every tree. 12 to 32 were level within the noise on WinSxS
    /// (1.58 to 1.75 s) and the source tree (1.28 to 1.42 s), and 24 and 32 led on Program Files
    /// (75 to 81 ms against 93 to 99). Nothing there earns a change from 16.</para>
    ///
    /// <para>The SATA SSD held too little to walk for a measurement, so it is not measured.</para>
    /// </summary>
    public static int WalkThreads(StorageMedia media) => WalkTuning.Default.Threads;

    /// <summary>
    /// 16 KiB on every kind. On NVMe it was fastest or level with the fastest on every tree measured
    /// when the buffer could first be set (see <see cref="WalkTuning.Default"/>): 64 KiB was no
    /// faster, and 1 MiB was four to nine times slower. The gain Microsoft describes from a larger
    /// buffer is on a network share, which was not measured.
    /// </summary>
    public static int ListingBufferKiB(StorageMedia media) => WalkTuning.Default.ListingBufferBytes / 1024;

    /// <summary>
    /// 1 MiB on NVMe, read eight at a time, 16 MiB on other solid state, read one at a time, and the
    /// conservative 1 MiB everywhere else.
    ///
    /// <para><b>One read at a time</b>, the <c>table</c> route was timed elevated at 64 KiB, 256 KiB,
    /// 1 MiB, 4 MiB and 16 MiB, two rounds of three runs, the second in reverse order. The median of
    /// the later runs:</para>
    ///
    /// <list type="bullet">
    /// <item>NVMe, 1.8 TB volume: 385 to 388 ms at 16 MiB, 421 to 444 ms at 4 MiB, 470 to 535 ms at
    /// 1 MiB.</item>
    /// <item>NVMe, system volume of 3.0 M records: 7.3 to 7.6 s at 16 MiB, 7.5 to 9.3 s at 4 MiB,
    /// 12.6 to 13.2 s at 1 MiB, with other work reading the same disk.</item>
    /// <item>SATA SSD: 452 to 456 ms at 16 MiB, 489 to 492 ms at 4 MiB, 601 to 622 ms at 1 MiB.</item>
    /// </list>
    ///
    /// <para>Below 1 MiB every drive was slower again.</para>
    ///
    /// <para><b>With reads in flight</b> (see <see cref="TableReadsInFlight"/>) the large read stops
    /// paying on NVMe, because the drive is kept busy by the reads outstanding rather than by the size
    /// of each. The index of the 1.8 TB volume, 814 k records of which 550 k in use, took 189 ms at
    /// 256 KiB, 166 ms at 1 MiB, 167 ms at 4 MiB and 181 ms at 16 MiB, eight reads in flight. A
    /// later round, alternating, gave 167 ms at 1 MiB against 178 ms at 16 MiB there, and 4.4 s
    /// against 4.4 s on the NVMe system volume, by then 5.6 M records. Against the one read at a
    /// time of 16 MiB this replaced, the index took 167 ms against 400 ms and 4.4 s against 6.8 s.
    /// Every buffer is held for the whole pass, one per read in flight and one per parse thread, so
    /// 16 MiB would also hold 192 MiB where 1 MiB holds 12.</para>
    ///
    /// <para>The SATA SSD's table was almost all free records, which the pass does not read, so it
    /// took 5 to 9 ms at every value and those runs favour none. It keeps the one read at a time and
    /// the size that were fastest when its whole table was read.</para>
    /// </summary>
    public static int TableReadKiB(StorageMedia media) => media switch
    {
        StorageMedia.SolidState => TableTuning.MaximumReadBytes / 1024,
        _ => TableTuning.Default.ReadBytes / 1024,
    };

    /// <summary>
    /// Eight on NVMe, and the conservative one, a read at a time, everywhere else.
    ///
    /// <para>The index of the 1.8 TB NVMe volume above, 1 MiB reads, took 311 ms with one read in
    /// flight, 179 to 187 ms with four, 165 to 176 ms with eight and 161 to 164 ms with sixteen.
    /// On the busy NVMe system volume of 3.9 M records it took 4.4 s with eight and 4.2 s with
    /// sixteen, against 8.9 s for the reader that read one at a time. Sixteen was 2 to 6 % faster
    /// than eight and holds eight more buffers, so eight is where adding reads stopped paying
    /// for what they hold.</para>
    /// </summary>
    public static int TableReadsInFlight(StorageMedia media) => media switch
    {
        StorageMedia.Nvme => 8,
        _ => TableTuning.Default.ReadsInFlight,
    };

    /// <summary>
    /// <c>min(processors, 4)</c>, for the machine rather than a kind of drive.
    ///
    /// <para>Measured on the 1.8 TB NVMe volume, the reads, not the parse, set the pace once the
    /// process was warm: its index took 169 ms parsed on one thread, 165 ms on two, 172 ms on four
    /// and 176 ms on eight. The first run in a process did not: 0.35 to 0.42 s on one thread
    /// against 0.18 to 0.25 s on two to four.</para>
    /// </summary>
    public static int TableParseThreads() => Math.Min(Environment.ProcessorCount, 4);
}
