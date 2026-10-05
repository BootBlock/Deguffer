using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The only thing in Deguffer that reads a volume's raw sectors. <c>StorageQueries</c> also opens
/// volumes and disks, with no access rights, to ask what storage they are, and reads nothing.
///
/// <para>Everything it reads goes through <see cref="IRawVolume"/>, so it is tested against a
/// synthesised volume image that refuses any read a disk with 4,096-byte sectors would refuse.
/// Reading <c>\\.\C:</c> itself requires administrator rights (§6.3), which a build agent does not
/// have.</para>
///
/// <para>Windows treats a volume handle as unbuffered, so every read here is a whole number of
/// sectors from a sector boundary, into memory aligned to one. A record need not be: Windows gives
/// a disk with 4,096-byte sectors 1,024-byte records, four to a sector.</para>
/// </summary>
public sealed partial class VolumeMftSource : IMftSource
{
    private readonly IRawVolume _volume;
    private readonly NtfsBootSector _geometry;
    private readonly MftExtentMap _extents;
    private readonly ClusterReader _readClusters;

    private VolumeMftSource(IRawVolume volume, NtfsBootSector geometry, MftExtentMap extents, MftBitmapPlacement? bitmap)
    {
        _volume = volume;
        _geometry = geometry;
        _extents = extents;
        _readClusters = TryReadClusters;
        Bitmap = bitmap;
    }

    public int BytesPerRecord => _geometry.BytesPerFileRecord;

    public long RecordCount => _extents.DataSize / _geometry.BytesPerFileRecord;

    /// <summary>
    /// Open the volume holding <paramref name="driveLetter"/> and read enough of it to serve
    /// records. Returns null with a reason on every expected failure — §5.5 requires the fallback
    /// to be observable, so "could not" always comes with "because".
    /// </summary>
    public static VolumeMftSource? TryOpen(char driveLetter, out FallbackReason reason)
    {
        var handle = OpenOverlapped($@"\\.\{char.ToUpperInvariant(driveLetter)}:");

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();

            // Opening a volume for raw read is an administrator operation, so this is the ordinary
            // outcome for an unelevated run rather than an anomaly (§6.3).
            reason = error is ErrorAccessDenied
                ? FallbackReason.NotElevated
                : FallbackReason.VolumeNotAddressable;

            return null;
        }

        return TryOpen(new VolumeHandle(handle), out reason);
    }

    /// <summary>
    /// Open <paramref name="path"/> for reading with overlapped I/O, which <see cref="VolumeHandle"/>
    /// needs. Internal so a test can open an ordinary file the way a volume is opened, which needs no
    /// administrator rights.
    /// </summary>
    internal static SafeFileHandle OpenOverlapped(string path) =>
        // FILE_SHARE_WRITE is not optional: the system volume is always open for writing by other
        // processes, and omitting it makes the open fail on exactly the drive that matters.
        CreateFile(path, GenericRead, FileShareRead | FileShareWrite, nint.Zero, OpenExisting, FileFlagOverlapped, nint.Zero);

    /// <summary>
    /// Read enough of <paramref name="volume"/> to serve records, taking ownership of it: it is
    /// disposed here on every exit that does not hand it to a source, a throw included, because a
    /// leaked raw handle keeps the volume open until finalisation.
    /// </summary>
    internal static VolumeMftSource? TryOpen(IRawVolume volume, out FallbackReason reason)
    {
        reason = FallbackReason.MasterFileTableIncomplete;
        VolumeMftSource? source = null;

        try
        {
            source = Initialise(volume, ref reason);
            return source;
        }
        catch (IOException)
        {
            // A volume that vanished mid-open, or one the driver will not serve raw reads from.
            reason = FallbackReason.VolumeNotAddressable;
            return null;
        }
        finally
        {
            if (source is null)
            {
                volume.Dispose();
            }
        }
    }

    private static VolumeMftSource? Initialise(IRawVolume volume, ref FallbackReason reason)
    {
        // Sized to the largest sector, not to the 512 bytes the boot sector occupies, because the
        // sector size is not known until this read has succeeded. Microsoft's rules for an unbuffered
        // read ask for a whole number of sectors, which 512 bytes is not on a 4,096-byte-sector disk.
        using var boot = new VolumeReadBuffer(BootReadBytes);

        if (volume.Read(boot.Span, 0) != boot.Length
            || !NtfsBootSector.TryParse(boot.Span, out var geometry))
        {
            reason = FallbackReason.NotNtfsVolume;
            return null;
        }

        // Record 0 is read in whole clusters rather than as one record, which can be shorter than
        // a sector. $MFT's extension records are read through the same volume, before the source
        // that would otherwise serve them exists.
        ClusterReader read = (first, destination) => ReadClusters(volume, geometry.BytesPerCluster, first, destination);
        var clusters = (geometry.BytesPerFileRecord + geometry.BytesPerCluster - 1) / geometry.BytesPerCluster;

        using var start = new VolumeReadBuffer(clusters * geometry.BytesPerCluster);
        var record0 = start.Span[..geometry.BytesPerFileRecord];

        if (!read(geometry.MftStartCluster, start.Span)
            || !MftExtentMapReader.TryRead(record0, geometry.BytesPerCluster, read, out var extents, out var bitmap))
        {
            reason = FallbackReason.MasterFileTableIncomplete;
            return null;
        }

        reason = FallbackReason.None;
        return new VolumeMftSource(volume, geometry, extents, bitmap);
    }

    public MftBitmapPlacement? Bitmap { get; }

    public int BatchLength(long firstRecord, int capacity) => Plan(firstRecord, capacity).Records;

    public int ReadBatch(long firstRecord, Span<byte> destination)
    {
        var plan = Plan(firstRecord, destination.Length / BytesPerRecord);

        return plan switch
        {
            { Records: 0 } => 0,
            { OneRecord: true } => ReadOneRecord(firstRecord, destination),

            // A short read is not fatal: whole records that did arrive are still usable, and the
            // caller resumes from where this batch stopped.
            _ => _volume.Read(destination[..(plan.Records * BytesPerRecord)], plan.Offset) / BytesPerRecord,
        };
    }

    public async ValueTask<int> ReadBatchAsync(long firstRecord, Memory<byte> destination, CancellationToken ct)
    {
        var plan = Plan(firstRecord, destination.Length / BytesPerRecord);

        return plan switch
        {
            { Records: 0 } => 0,

            // Read a cluster at a time, and only for the few records nothing else can serve, so not
            // worth overlapping.
            { OneRecord: true } => ReadOneRecord(firstRecord, destination.Span),
            _ => await _volume.ReadAsync(destination[..(plan.Records * BytesPerRecord)], plan.Offset, ct).ConfigureAwait(false)
                / BytesPerRecord,
        };
    }

    private int ReadOneRecord(long number, Span<byte> destination) =>
        _extents.TryReadRecord(number, BytesPerCluster, _readClusters, destination[..BytesPerRecord]) ? 1 : 0;

    /// <summary>
    /// How many records a read from <paramref name="firstRecord"/> serves into room for
    /// <paramref name="capacity"/>, and the byte offset it reads them from. <c>OneRecord</c> is a
    /// record read in whole clusters, because no whole-sector read can serve it.
    /// </summary>
    private (int Records, long Offset, bool OneRecord) Plan(long firstRecord, int capacity)
    {
        if (capacity <= 0 || firstRecord < 0 || firstRecord >= RecordCount)
        {
            return default;
        }

        // Never rounded to an earlier boundary to make the read aligned. That would shift which
        // record the batch starts at, and the caller numbers records by position — so every record
        // after the first gap would be attributed to the wrong parent.
        var streamOffset = firstRecord * BytesPerRecord;
        var virtualCluster = streamOffset / _geometry.BytesPerCluster;
        var withinCluster = streamOffset % _geometry.BytesPerCluster;

        if (!_extents.TryTranslate(virtualCluster, out var physicalCluster, out var contiguousClusters))
        {
            return default;
        }

        var contiguousBytes = (contiguousClusters * _geometry.BytesPerCluster) - withinCluster;
        var remainingRecords = Math.Min(capacity, RecordCount - firstRecord);
        var offset = (physicalCluster * _geometry.BytesPerCluster) + withinCluster;

        // Rounded down to whole records so a batch never ends mid-record, and to whole sectors so
        // the read is one the volume serves. Where the next read starts is the planner's choice,
        // which puts it on a sector boundary: see MftBatchPlanner.
        var recordsPerSector = Math.Max(1, _geometry.BytesPerSector / BytesPerRecord);
        var wholeRecords = Math.Min(contiguousBytes / BytesPerRecord, remainingRecords);
        wholeRecords -= wholeRecords % recordsPerSector;

        // One record at a time, in whole clusters, where no whole-sector read can serve the batch:
        // a record spanning a gap between extents, which happens whenever a cluster is smaller than
        // a record; the last few records of a table that ends part-way through a sector; and a
        // batch starting inside a sector after a short read. Returning nothing here would be
        // indistinguishable from an unreadable table, and would send the whole volume to the walk.
        return wholeRecords == 0 || offset % _geometry.BytesPerSector != 0
            ? (1, 0, OneRecord: true)
            : ((int)wholeRecords, offset, OneRecord: false);
    }

    public int BytesPerCluster => _geometry.BytesPerCluster;

    public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
        ReadClusters(_volume, BytesPerCluster, firstCluster, destination);

    private static bool ReadClusters(IRawVolume volume, int bytesPerCluster, long firstCluster, Span<byte> destination)
    {
        // Whole clusters only, so the read stays sector aligned as a raw volume handle requires. The
        // cluster comes from a run list on the disk, so its byte offset is bounded by division
        // before it is formed: a corrupt run naming a cluster near 2^63 would otherwise wrap the
        // offset negative and throw out of the read.
        if (firstCluster < 0
            || destination.Length % bytesPerCluster != 0
            || firstCluster > (long.MaxValue - destination.Length) / bytesPerCluster)
        {
            return false;
        }

        return volume.Read(destination, firstCluster * bytesPerCluster) == destination.Length;
    }

    public void Dispose() => _volume.Dispose();

    /// <summary>Internal so a test can hold it to every sector size the boot sector accepts.</summary>
    internal const int BootReadBytes = NtfsBootSector.MaximumBytesPerSector;

    private const uint GenericRead = 0x8000_0000;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const int ErrorAccessDenied = 5;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
