using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// The only thing in Deguffer that opens a raw volume handle.
///
/// Everything above it — the parser, the extent map, the index, the aggregation — works on spans
/// and is tested against synthesised records, because this class cannot be: reading
/// <c>\\.\C:</c> requires administrator rights (§6.3), which a build agent does not have.
/// </summary>
public sealed partial class VolumeMftSource : IMftSource
{
    private readonly SafeFileHandle _volume;
    private readonly NtfsBootSector _geometry;
    private readonly MftExtentMap _extents;

    private VolumeMftSource(SafeFileHandle volume, NtfsBootSector geometry, MftExtentMap extents)
    {
        _volume = volume;
        _geometry = geometry;
        _extents = extents;
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
        reason = FallbackReason.MasterFileTableIncomplete;

        // FILE_SHARE_WRITE is not optional: the system volume is always open for writing by other
        // processes, and omitting it makes the open fail on exactly the drive that matters.
        var handle = CreateFile(
            $@"\\.\{char.ToUpperInvariant(driveLetter)}:",
            GenericRead,
            FileShareRead | FileShareWrite,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);

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

        // The handle is released here and nowhere else, on every exit that does not hand it to a
        // source, a throw included: a leaked raw handle keeps the volume open until finalisation.
        VolumeMftSource? source = null;

        try
        {
            source = Initialise(handle, ref reason);
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
                handle.Dispose();
            }
        }
    }

    private static VolumeMftSource? Initialise(SafeFileHandle handle, ref FallbackReason reason)
    {
        // Sized to the largest sector, not to the 512 bytes the boot sector occupies, because the
        // sector size is not known until this read has succeeded. Microsoft's rules for an unbuffered
        // read ask for a whole number of sectors, which 512 bytes is not on a 4,096-byte-sector disk.
        using var boot = new VolumeReadBuffer(BootReadBytes);

        if (RandomAccess.Read(handle, boot.Span, 0) != boot.Length
            || !NtfsBootSector.TryParse(boot.Span, out var geometry))
        {
            reason = FallbackReason.NotNtfsVolume;
            return null;
        }

        using var record0 = new VolumeReadBuffer(geometry.BytesPerFileRecord);
        var offset = geometry.MftStartCluster * geometry.BytesPerCluster;

        // $MFT's extension records are read through the same handle, before the source that would
        // otherwise serve them exists.
        if (RandomAccess.Read(handle, record0.Span, offset) != record0.Length
            || !MftExtentMapReader.TryRead(
                record0.Span,
                geometry.BytesPerCluster,
                (first, destination) => ReadClusters(handle, geometry.BytesPerCluster, first, destination),
                out var extents))
        {
            reason = FallbackReason.MasterFileTableIncomplete;
            return null;
        }

        reason = FallbackReason.None;
        return new VolumeMftSource(handle, geometry, extents);
    }

    public int ReadBatch(long firstRecord, Span<byte> destination)
    {
        var capacity = destination.Length / BytesPerRecord;
        if (capacity == 0 || firstRecord >= RecordCount)
        {
            return 0;
        }

        // No alignment adjustment is needed or wanted here. A raw volume read must be sector
        // aligned, and record boundaries always are: the boot sector parse guarantees the record
        // size is a power of two no smaller than a sector. Rounding to clusters instead would shift
        // which record the batch starts at, and the caller numbers records by position — so every
        // record after the first gap would be attributed to the wrong parent.
        var streamOffset = firstRecord * BytesPerRecord;
        var virtualCluster = streamOffset / _geometry.BytesPerCluster;
        var withinCluster = streamOffset % _geometry.BytesPerCluster;

        if (!_extents.TryTranslate(virtualCluster, out var physicalCluster, out var contiguousClusters))
        {
            return 0;
        }

        var contiguousBytes = (contiguousClusters * _geometry.BytesPerCluster) - withinCluster;
        var remainingRecords = Math.Min(capacity, RecordCount - firstRecord);
        var offset = (physicalCluster * _geometry.BytesPerCluster) + withinCluster;

        // Where a record spans the gap between two extents — possible whenever a cluster is smaller
        // than a record — no contiguous read can produce it. Splicing it together is what keeps a
        // legitimately fragmented volume on the fast path: returning nothing here would be
        // indistinguishable from an unreadable table, and would send the whole volume to the walk.
        if (contiguousBytes < BytesPerRecord)
        {
            return TryReadStraddlingRecord(destination, offset, (int)contiguousBytes, virtualCluster + contiguousClusters);
        }

        // Rounded down to whole records so a batch never ends mid-record: the caller advances by
        // the returned count, and a trailing fragment would leave it re-reading from an offset the
        // fragment already consumed.
        var wholeRecords = Math.Min(contiguousBytes / BytesPerRecord, remainingRecords);
        var bytes = (int)(wholeRecords * BytesPerRecord);

        // A short read is not fatal: whole records that did arrive are still usable, and the
        // caller resumes from where this batch stopped.
        return RandomAccess.Read(_volume, destination[..bytes], offset) / BytesPerRecord;
    }

    /// <summary>
    /// Read one record whose bytes are split across two extents, returning 1 on success and 0 if
    /// the second half cannot be located.
    /// </summary>
    private int TryReadStraddlingRecord(Span<byte> destination, long offset, int head, long nextVirtualCluster)
    {
        if (head <= 0 || !_extents.TryTranslate(nextVirtualCluster, out var nextCluster, out _))
        {
            return 0;
        }

        var tail = BytesPerRecord - head;

        if (RandomAccess.Read(_volume, destination[..head], offset) != head)
        {
            return 0;
        }

        var read = RandomAccess.Read(
            _volume,
            destination.Slice(head, tail),
            nextCluster * _geometry.BytesPerCluster);

        return read == tail ? 1 : 0;
    }

    public int BytesPerCluster => _geometry.BytesPerCluster;

    public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
        ReadClusters(_volume, BytesPerCluster, firstCluster, destination);

    private static bool ReadClusters(SafeFileHandle volume, int bytesPerCluster, long firstCluster, Span<byte> destination)
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

        return RandomAccess.Read(volume, destination, firstCluster * bytesPerCluster) == destination.Length;
    }

    public void Dispose() => _volume.Dispose();

    /// <summary>Internal so a test can hold it to every sector size the boot sector accepts.</summary>
    internal const int BootReadBytes = NtfsBootSector.MaximumBytesPerSector;

    private const uint GenericRead = 0x8000_0000;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
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
