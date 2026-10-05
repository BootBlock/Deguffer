namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How hard a read of the file table drives the disk: how many bytes of records each read asks for.
///
/// <para>The value changes how fast the table is read, never what is read. Every record is still
/// read and numbered by its place in the table, and a short read still ends the pass rather than
/// being skipped past, whatever the size. See <see cref="MftRecordStream"/>.</para>
/// </summary>
public sealed record TableTuning
{
    /// <summary>
    /// The largest record NTFS formats, so every read holds at least one record, and the largest
    /// sector, so a read on a 4,096-byte-sector disk can be served in whole sectors.
    /// </summary>
    public const int MinimumReadBytes = 4 * 1024;

    /// <summary>
    /// Sixteen times what the table was read with before the size could be set. The buffer is
    /// unmanaged memory held for the whole pass, so the bound is also the memory a setting can ask
    /// for.
    /// </summary>
    public const int MaximumReadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 1,024 records of 1 KiB, which is what the table was read with before the size could be set.
    /// </summary>
    public static readonly TableTuning Default = new(1024 * 1024);

    public TableTuning(int readBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readBytes, MinimumReadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readBytes, MaximumReadBytes);

        ReadBytes = readBytes;
    }

    /// <summary>How many bytes of records each read asks for, before it is cut to whole records.</summary>
    public int ReadBytes { get; }

    /// <summary>How many records of <paramref name="bytesPerRecord"/> one read asks for.</summary>
    public int RecordsPerRead(int bytesPerRecord) => Math.Max(1, ReadBytes / bytesPerRecord);
}
