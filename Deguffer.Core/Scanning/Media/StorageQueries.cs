using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Media;

/// <inheritdoc />
public sealed partial class StorageQueries : IStorageQueries
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between calls.</summary>
    public static readonly StorageQueries Machine = new();

    /// <summary>
    /// <c>CTL_CODE(IOCTL_VOLUME_BASE, 0, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>. Any access, which is
    /// why a handle opened with none can send it.
    /// </summary>
    private const uint VolumeGetVolumeDiskExtents = 0x0056_0000;

    /// <summary><c>CTL_CODE(IOCTL_STORAGE_BASE, 0x500, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>.</summary>
    private const uint StorageQueryProperty = 0x002D_1400;

    private const int StorageAdapterProperty = 1;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    /// <summary>
    /// <c>STORAGE_PROPERTY_QUERY</c>: the property, <c>PropertyStandardQuery</c> (0), and one byte of
    /// additional parameters, padded to 12.
    /// </summary>
    private const int PropertyQueryBytes = 12;

    /// <summary>
    /// Room for four extents, which every simple volume and most spanned ones fit. A volume with
    /// more answers <c>ERROR_MORE_DATA</c> with its count, and is asked again with room for all.
    /// </summary>
    private const int FirstExtentsBytes = StorageDescriptors.ExtentsHeaderBytes + (4 * StorageDescriptors.ExtentBytes);

    /// <summary>
    /// Larger than either descriptor asked for: the seek-penalty one is 12 bytes and the adapter one
    /// 32. A driver writes no more than the structure, and reports how much it wrote.
    /// </summary>
    private const int DescriptorBytes = 64;

    /// <summary>A volume name is <c>\\?\Volume{GUID}\</c>: 49 characters and a terminator.</summary>
    private const int VolumeNameLength = 50;

    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;
    private const int ErrorMoreData = 234;

    private StorageQueries()
    {
    }

    public StorageAnswer DiskExtents(string mountPoint)
    {
        // The volume's own name rather than \\.\X:, because a volume mounted at a folder has no
        // letter, and this is the name that opens a volume wherever it is mounted.
        if (VolumeNameOf(mountPoint, out var error) is not { } volumeName)
        {
            return StorageAnswer.Failed(error);
        }

        // The trailing separator names the volume's root directory. Without it the name opens the
        // volume device, which is what the query is sent to.
        using var volume = Open(volumeName.TrimEnd('\\'));

        if (volume.IsInvalid)
        {
            return StorageAnswer.Failed(Marshal.GetLastPInvokeError());
        }

        var first = Query(volume, VolumeGetVolumeDiskExtents, [], FirstExtentsBytes);

        if (first.Win32Error != ErrorMoreData)
        {
            return first;
        }

        // The header is written even when the extents do not fit, so the count sizes the retry.
        return first.Bytes is { } header && StorageDescriptors.TryReadExtentCount(header, out var count)
            ? Query(volume, VolumeGetVolumeDiskExtents, [], StorageDescriptors.ExtentsHeaderBytes + (count * StorageDescriptors.ExtentBytes))
            : first;
    }

    public StorageAnswer Adapter(int disk) => Property(disk, StorageAdapterProperty);

    public StorageAnswer SeekPenalty(int disk) => Property(disk, StorageDeviceSeekPenaltyProperty);

    private static StorageAnswer Property(int disk, int property)
    {
        using var device = Open($@"\\.\PhysicalDrive{disk}");

        if (device.IsInvalid)
        {
            return StorageAnswer.Failed(Marshal.GetLastPInvokeError());
        }

        var query = new byte[PropertyQueryBytes];
        BitConverter.TryWriteBytes(query, property);

        return Query(device, StorageQueryProperty, query, DescriptorBytes);
    }

    /// <summary>
    /// No access rights requested, which is enough for both queries (they are
    /// <c>FILE_ANY_ACCESS</c>) and needs no administrator. Shared for reading and writing, because
    /// the system volume and its disk are always open for both.
    /// </summary>
    private static SafeFileHandle Open(string device) =>
        CreateFile(device, 0, FileShareRead | FileShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);

    /// <summary>
    /// Sends <paramref name="code"/> and returns what came back. On <c>ERROR_MORE_DATA</c> the bytes
    /// written so far come back with the error, because they are what says how much room to give.
    /// </summary>
    private static unsafe StorageAnswer Query(SafeFileHandle device, uint code, byte[] input, int outputBytes)
    {
        var output = new byte[outputBytes];
        bool answered;
        uint written;

        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        {
            answered = DeviceIoControl(
                device, code, inputPointer, (uint)input.Length, outputPointer, (uint)output.Length, out written, nint.Zero);
        }

        if (answered)
        {
            return StorageAnswer.Answered(output[..(int)written]);
        }

        var error = Marshal.GetLastPInvokeError();

        return error == ErrorMoreData
            ? new StorageAnswer(output, error)
            : StorageAnswer.Failed(error);
    }

    private static unsafe string? VolumeNameOf(string mountPoint, out int error)
    {
        var buffer = stackalloc char[VolumeNameLength];

        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, VolumeNameLength))
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        return new string(buffer);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte* inBuffer,
        uint inBufferSize,
        byte* outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeNameForVolumeMountPointW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint, char* volumeName, uint bufferLength);
}
