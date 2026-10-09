using System.Runtime.InteropServices;
using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>What one file occupies on the disk, as the file system states it.</summary>
/// <param name="Bytes">The space it takes. Zero for a link, whose target is counted where it is.</param>
/// <param name="IsLink">
/// Whether the file is a symbolic link or another name surrogate rather than content of its own.
/// </param>
public readonly record struct Occupancy(long Bytes, bool IsLink);

/// <summary>
/// Asks the file system what a file occupies, for the few files a listing cannot size: the walk
/// learns lengths alone, and a file that is in the cloud, compressed or sparse occupies less.
///
/// <para>A seam so the walk's handling of those files is provable without a cloud provider, a
/// compressed volume or a sparse file on the machine running the tests.</para>
/// </summary>
public interface IOccupancyProbe
{
    /// <summary>
    /// What the file at <paramref name="path"/> occupies, or null where Windows would not say: it
    /// went away since it was listed, or it was refused.
    /// </summary>
    /// <param name="path">The file, in the extended form §6.3 requires.</param>
    /// <param name="attributes">What the listing reported for it, which says whether it may be a link.</param>
    Occupancy? Measure(string path, FileAttributes attributes);
}

/// <summary>
/// <see cref="IOccupancyProbe"/> against the real file system. Stateless, so one instance serves
/// every walk (G5).
///
/// <para><b>It never reads a file's data</b>, so it never starts a cloud download: both calls open
/// for attributes only. A scratch sync root showed that an online-only placeholder answers 0 here and
/// asks its provider for nothing.</para>
///
/// <para><b>A link is told apart before it is measured.</b> <c>GetCompressedFileSize</c> follows a
/// symbolic link and would answer for its target, which keeps its own place in the tree, so the tag
/// is read first from the link itself.</para>
/// </summary>
public sealed partial class OccupancyProbe : IOccupancyProbe
{
    public static readonly OccupancyProbe Default = new();

    private const uint InvalidFileSize = 0xFFFF_FFFF;
    private const uint NameSurrogateBit = 0x2000_0000;

    private OccupancyProbe()
    {
    }

    public Occupancy? Measure(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (ReparseTagOf(path) is not { } tag)
            {
                return null;
            }

            if ((tag & NameSurrogateBit) != 0)
            {
                return new Occupancy(0, IsLink: true);
            }
        }

        return OnDisk(path) is { } bytes ? new Occupancy(bytes, IsLink: false) : null;
    }

    /// <summary>
    /// The space a file takes on its drive, which for a cloud, compressed or sparse file is less
    /// than its length. Null where Windows would not say.
    /// </summary>
    /// <param name="path">The file, in the extended form, since nothing here adds the prefix (§6.3).</param>
    public static long? OnDisk(string path)
    {
        var low = GetCompressedFileSize(path, out var high);

        // The low half can be all ones legitimately, so only the error code says it failed.
        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    /// <summary>The reparse tag of the entry itself, never its target's, or null where it would not open.</summary>
    private static uint? ReparseTagOf(string path)
    {
        using var handle = FileInformation.Open(path, HandleUse.Describe);

        return !handle.IsInvalid && FileInformation.TryAttributeTag(handle, out var info) ? info.ReparseTag : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}
