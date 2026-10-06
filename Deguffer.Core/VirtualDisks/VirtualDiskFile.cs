using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// What the host's file system says about one virtual disk file.
/// </summary>
/// <param name="Presence">Whether the file is there. A refusal is not an absence.</param>
/// <param name="Length">The file's length, which is the most the disk has ever needed. Zero unless present.</param>
/// <param name="OnDisk">
/// The space the file takes on the drive, or null where Windows would not say. Smaller than
/// <paramref name="Length"/> only for a sparse or compressed file.
/// </param>
/// <param name="IsSparse">Whether the file is sparse, which changes the route that makes it smaller.</param>
public sealed record VirtualDiskSize(PathPresence Presence, long Length, long? OnDisk, bool IsSparse)
{
    /// <summary>The figure to report as the space the disk takes on the drive.</summary>
    public long Taken => OnDisk ?? Length;
}

/// <summary>
/// Measures a virtual disk file from outside, without opening it.
///
/// <para>A disk WSL or Docker is running from is held open by the virtual machine, so a read that
/// opened the file for its contents would be refused. The two calls here ask the file system about the
/// file rather than open it, and both answer for a file in use.</para>
///
/// <para>Its output is sizes and a flag, so no test can show from its result whether the path it asked
/// with carried the <c>\\?\</c> prefix; it asks with <see cref="LongPath.Extended"/> regardless.</para>
/// </summary>
public static partial class VirtualDiskFile
{
    private const uint InvalidFileSize = 0xFFFFFFFF;

    public static VirtualDiskSize Measure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var presence = LongPath.ProbeFile(path);

        if (presence != PathPresence.Present)
        {
            return new VirtualDiskSize(presence, 0, null, false);
        }

        var extended = LongPath.Extended(path);

        FileInfo file;

        try
        {
            file = new FileInfo(extended);
            file.Refresh();
            _ = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present a moment ago and not describable now: removed in between, or refused. Either
            // way there is no figure, and an absence is the claim that would be false.
            return new VirtualDiskSize(PathPresence.Refused, 0, null, false);
        }

        return new VirtualDiskSize(
            PathPresence.Present,
            file.Length,
            OnDiskSize(extended),
            file.Attributes.HasFlag(FileAttributes.SparseFile));
    }

    /// <summary>
    /// The space the file takes on the drive, which for a sparse or compressed file is less than its
    /// length. Null where Windows would not say.
    /// </summary>
    private static long? OnDiskSize(string extended)
    {
        var low = GetCompressedFileSize(extended, out var high);

        // The low half can be all ones legitimately, so only the error code says it failed.
        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}
