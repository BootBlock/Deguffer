using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning.Media;

/// <summary>
/// Opening a storage device and sending it one control code, for the types in Core that ask a disk
/// something. One route to a device, and one rule for what a failed query carries.
/// </summary>
internal static partial class DeviceCalls
{
    /// <summary><c>CTL_CODE(IOCTL_STORAGE_BASE, 0x500, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>.</summary>
    internal const uint StorageQueryProperty = 0x002D_1400;

    /// <summary>
    /// <c>STORAGE_PROPERTY_QUERY</c>: the property, <c>PropertyStandardQuery</c> (0), and one byte of
    /// additional parameters, padded to 12.
    /// </summary>
    internal const int PropertyQueryBytes = 12;

    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint OpenExisting = 3;

    /// <summary>The name Windows opens physical disk <paramref name="disk"/> by.</summary>
    internal static string DiskName(int disk) => $@"\\.\PhysicalDrive{disk}";

    /// <summary>A standard query for <paramref name="property"/>, a <c>STORAGE_PROPERTY_ID</c>.</summary>
    internal static byte[] PropertyQuery(int property)
    {
        var query = new byte[PropertyQueryBytes];
        BitConverter.TryWriteBytes(query, property);
        return query;
    }

    /// <summary>
    /// Opens <paramref name="device"/> with no access rights requested, which is enough for a
    /// <c>FILE_ANY_ACCESS</c> query and needs no administrator. Invalid where it would not open, with
    /// the reason in the last P/Invoke error.
    /// </summary>
    internal static SafeFileHandle Open(string device) => Create(device, 0);

    /// <summary>
    /// Opens <paramref name="device"/> so that Windows will pass it an ATA command, which it does
    /// only through a handle with these rights, and which only an administrator can open on a disk.
    ///
    /// <para>The rights are the driver's entry condition. Nothing sent through such a handle may
    /// write to the drive: <c>DriveRequestsTests</c> pins every command, and
    /// <c>DriveHealthSourcesTests</c> holds this method to its one caller.</para>
    /// </summary>
    internal static SafeFileHandle OpenForCommands(string device) => Create(device, GenericRead | GenericWrite);

    /// <summary>
    /// Sends <paramref name="code"/> and returns what the driver wrote into
    /// <paramref name="output"/>, cut to the length it reported, or the error alone. A failure never
    /// carries bytes, because bytes a driver did not finish writing read as zeroes, and a zero is an
    /// answer to every field read from these structures.
    /// </summary>
    internal static unsafe StorageAnswer Query(SafeFileHandle device, uint code, byte[] input, byte[] output)
    {
        bool answered;
        uint written;

        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        {
            answered = DeviceIoControl(
                device, code, inputPointer, (uint)input.Length, outputPointer, (uint)output.Length, out written, nint.Zero);
        }

        return answered
            ? StorageAnswer.Answered(output[..(int)written])
            : StorageAnswer.Failed(Marshal.GetLastPInvokeError());
    }

    /// <summary>Shared for reading and writing, because the system volume and its disk are always open for both.</summary>
    private static SafeFileHandle Create(string device, uint access) =>
        CreateFile(device, access, FileShareRead | FileShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);

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
}
