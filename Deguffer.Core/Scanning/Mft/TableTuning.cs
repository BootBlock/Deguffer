namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How hard a pass over the file table drives the disk and the processor: how many bytes each read
/// asks for, how many reads are outstanding at once, how many threads parse what arrives, and how
/// many reads of the attribute lists kept outside the table are outstanding at once, and whether
/// those reads wait for the first pass to end.
///
/// <para>The values change how fast the table is read, never what is read. Every record in use is
/// still read and numbered by its place in the table, and a short read still ends the pass rather
/// than being skipped past, whatever the values. See <see cref="MftReadPass"/>.</para>
///
/// <para>Memory is <c>(ReadsInFlight + ParseThreads) × ReadBytes</c> of unmanaged buffers, held for
/// the whole pass. A buffer is held from its read until its records are parsed, and a buffer being
/// parsed is not a read in flight, so each parse thread has a buffer of its own beside the reads.
/// At the largest of every value that is 1.5 GiB, which is what somebody setting all three to their
/// largest asks for.</para>
///
/// <para>The lists kept outside the table add <c>ListReadsInFlight</c> buffers, each as large as the
/// largest read made into it, after the first pass and beside its buffers. A buffer holds whole
/// clusters, and a read, of one list or of several close together, is never more than
/// <see cref="MftAttributeList.MaximumLength"/> rounded up to whole clusters, so each is at most the
/// larger of that and a cluster: 8 MiB at the largest depth on a volume of 4 KiB clusters,
/// and 64 MiB on one of 2 MiB clusters. The entries of lists read ahead of their turn wait beside
/// them, at most four times as many reads' worth. A list's entries take less than three and a half
/// times the clusters it was read from: an entry is 40 bytes held and at least 26 on the disk, in a
/// list that grows by doubling. Read beside the first pass
/// (<see cref="AttributeListReads.BesideFirstPass"/>), they are held while it runs as well.</para>
/// </summary>
public sealed record TableTuning
{
    /// <summary>
    /// The largest record NTFS formats, so every read holds at least one record, and the largest
    /// sector, so a read on a 4,096-byte-sector disk can be served in whole sectors. A size that is
    /// not a whole number of sectors is cut to one by the source, so no read is ever a part sector.
    /// </summary>
    public const int MinimumReadBytes = 4 * 1024;

    /// <summary>Sixteen times what the table was read with before the size could be set.</summary>
    public const int MaximumReadBytes = 16 * 1024 * 1024;

    public const int MinimumReadsInFlight = 1;

    /// <summary>
    /// The deepest queue a SATA drive takes. On NVMe, sixteen was the most measured, and gained
    /// little over eight.
    /// </summary>
    public const int MaximumReadsInFlight = 32;

    public const int MinimumParseThreads = 1;

    public const int MinimumListReadsInFlight = 1;

    /// <summary>The deepest queue a SATA drive takes, as for <see cref="MaximumReadsInFlight"/>.</summary>
    public const int MaximumListReadsInFlight = 32;

    public const int MinimumListBatch = 1;

    /// <summary>More lists than a system volume of a few million records holds outside its table.</summary>
    public const int MaximumListBatch = 1024 * 1024;

    /// <summary>
    /// What a batch beside the first pass holds: the fastest measured on NVMe, against a batch as
    /// large as the reads in flight (see <see cref="AutoTuning.TableListReads"/>).
    /// </summary>
    public const int DefaultListBatch = 4096;

    /// <summary>As many as the walk may run, <see cref="WalkTuning.MaximumThreads"/>.</summary>
    public const int MaximumParseThreads = 64;

    /// <summary>
    /// What the table was read with before these could be set: 1,024 records of 1 KiB, one read at a
    /// time, parsed on the thread that read them, and one list at a time once the first pass has
    /// ended. A kind of drive nobody has
    /// measured gets the read size and reads in flight, and the machine's parse threads: see
    /// <see cref="AutoTuning"/>.
    /// </summary>
    public static readonly TableTuning Default = new(1024 * 1024, readsInFlight: 1, parseThreads: 1, listReadsInFlight: 1);

    public TableTuning(
        int readBytes,
        int readsInFlight,
        int parseThreads,
        int listReadsInFlight,
        AttributeListReads listReads = AttributeListReads.AfterFirstPass,
        int listBatch = DefaultListBatch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readBytes, MinimumReadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readBytes, MaximumReadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(readsInFlight, MinimumReadsInFlight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readsInFlight, MaximumReadsInFlight);
        ArgumentOutOfRangeException.ThrowIfLessThan(parseThreads, MinimumParseThreads);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parseThreads, MaximumParseThreads);
        ArgumentOutOfRangeException.ThrowIfLessThan(listReadsInFlight, MinimumListReadsInFlight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listReadsInFlight, MaximumListReadsInFlight);

        if (!Enum.IsDefined(listReads))
        {
            throw new ArgumentOutOfRangeException(nameof(listReads), listReads, "Not a time the lists can be read at.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(listBatch, MinimumListBatch);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listBatch, MaximumListBatch);

        ReadBytes = readBytes;
        ReadsInFlight = readsInFlight;
        ParseThreads = parseThreads;
        ListReadsInFlight = listReadsInFlight;
        ListReads = listReads;
        ListBatch = listBatch;
    }

    /// <summary>How many bytes of records each read asks for, at most, before it is cut to whole records and sectors.</summary>
    public int ReadBytes { get; }

    /// <summary>How many reads are outstanding at once, at most.</summary>
    public int ReadsInFlight { get; }

    /// <summary>How many threads parse records at once, the calling thread included.</summary>
    public int ParseThreads { get; }

    /// <summary>
    /// How many reads of the attribute lists kept outside the table are outstanding at once, at most.
    /// A read is one list, or several close together (<see cref="MftListReadPlanner"/>). Apart from
    /// <see cref="ReadsInFlight"/> because the reads are the opposite kind: a few clusters each,
    /// scattered over the volume, where a read of the table is megabytes in one place. A drive
    /// reorders and overlaps small reads only when it is given several at once.
    /// </summary>
    public int ListReadsInFlight { get; }

    /// <summary>
    /// Whether the lists kept outside the table are read once the first pass has ended, or while it
    /// reads. Only how soon they are read: never which are read, nor what is made of them.
    /// </summary>
    public AttributeListReads ListReads { get; }

    /// <summary>
    /// How many lists a batch read beside the first pass waits for before it is read, where
    /// <see cref="ListReads"/> is <see cref="AttributeListReads.BesideFirstPass"/>; the last batch is
    /// whatever is left once the pass ends. A small batch is read soon after its lists are parsed, but
    /// lists close together on the disk land in different batches and are read apart, and its reads
    /// come in short bursts that the table's reads wait behind.
    /// </summary>
    public int ListBatch { get; }

    /// <summary>How many records of <paramref name="bytesPerRecord"/> one read asks for.</summary>
    public int RecordsPerRead(int bytesPerRecord) => Math.Max(1, ReadBytes / bytesPerRecord);
}
