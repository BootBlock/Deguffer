using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Opens a named stream of a file through a handle already held on the file, so the stream read is
/// that file's whatever its path names by then.
///
/// <para><c>NtCreateFile</c> with the held handle as the root and the stream's name alone as the
/// object's name opens the stream relative to the file, which walks no path: a path open of
/// <c>file:stream</c> would walk every folder again and follow any link put on the way since. A
/// handle opened by number serves as the root as well as one opened by path, measured on NTFS.</para>
/// </summary>
internal static unsafe partial class NamedStreams
{
    private const uint GenericRead = 0x8000_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint ShareRead = 0x0001;
    private const uint ShareDelete = 0x0004;
    private const uint FileOpen = 1;
    private const uint SynchronousIoNonAlert = 0x0020;
    private const uint NonDirectoryFile = 0x0040;

    /// <summary><c>OBJ_CASE_INSENSITIVE</c>: NTFS names streams without regard to case.</summary>
    private const uint CaseInsensitive = 0x0040;

    /// <summary>
    /// Opens the stream <paramref name="name"/> (as <see cref="FileInformation.StreamsOf"/> lists it)
    /// of the file <paramref name="file"/> is open on, to read it, sharing reading and, where
    /// <paramref name="shareDelete"/> says so, deleting, but never writing. Null where Windows would
    /// not open it.
    /// </summary>
    public static SafeFileHandle? Open(SafeFileHandle file, string name, bool shareDelete)
    {
        fixed (char* chars = name)
        {
            var objectName = new UnicodeString
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = chars,
            };

            var added = false;

            try
            {
                file.DangerousAddRef(ref added);

                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = file.DangerousGetHandle(),
                    ObjectName = &objectName,
                    Attributes = CaseInsensitive,
                };

                var status = NtCreateFile(
                    out var stream,
                    GenericRead | Synchronize,
                    &attributes,
                    out _,
                    allocationSize: 0,
                    fileAttributes: 0,
                    shareDelete ? ShareRead | ShareDelete : ShareRead,
                    FileOpen,
                    SynchronousIoNonAlert | NonDirectoryFile,
                    extendedAttributes: 0,
                    extendedAttributesLength: 0);

                if (status >= 0)
                {
                    return stream;
                }

                stream.Dispose();
                return null;
            }
            finally
            {
                if (added)
                {
                    file.DangerousRelease();
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    /// <summary><c>IO_STATUS_BLOCK</c>: a status or pointer, then the information it returns.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(
        out SafeFileHandle file,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        out IoStatusBlock ioStatus,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint extendedAttributes,
        uint extendedAttributesLength);
}
