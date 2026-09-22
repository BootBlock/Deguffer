using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// A path's attributes, or the Win32 error that says why Windows gave none, read the way
/// <see cref="File.GetAttributes(string)"/> reads them and without the exception it throws.
///
/// <para><b>Why not the framework's call.</b> Every presence question in Core is answered by an
/// attribute read, and most of them are asked about paths that are not there: a <c>PATH</c> search
/// tries each directory against each extension, and a provider's discovery asks after every
/// location a tool might use. The framework throws for each of those, so opening the Explore page
/// raised more than five thousand exceptions to say "absent" — a cost on every probe, and a
/// debugger's output buried under them.</para>
///
/// <para><b>What it copies from the framework, because an unchanged answer is the point.</b> Around
/// a hundred call sites read presence through <see cref="LongPath"/>, several of them guarding a
/// deletion, so this has to fail exactly where the framework fails. It trims a trailing separator
/// first, as the framework does, because neither Win32 call accepts one. It turns off the
/// insert-media prompt for the length of the read, so an empty card reader answers rather than
/// raising a dialog. And it asks again through <c>FindFirstFileExW</c> when the first read fails
/// for any reason but an unreachable path: a file pending deletion answers
/// <c>ERROR_ACCESS_DENIED</c> and <c>pagefile.sys</c> answers <c>ERROR_SHARING_VIOLATION</c>, and the
/// directory's own index describes both.</para>
/// </summary>
internal static partial class FileAttributeRead
{
    private const int Success = 0;

    /// <summary>
    /// The two errors the framework throws <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> for, which the callers that fail closed read as the
    /// only certain absence.
    /// </summary>
    internal const int FileNotFound = 2;

    /// <inheritdoc cref="FileNotFound"/>
    internal const int PathNotFound = 3;

    private const uint FailCriticalErrors = 0x0001;

    private const int GetFileExInfoStandard = 0;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;

    private static readonly nint InvalidHandle = -1;

    /// <summary>Read the attributes of <paramref name="path"/>.</summary>
    /// <param name="path">A fully qualified path. The caller decides whether it is extended.</param>
    /// <param name="attributes">What Windows reported, or default where it reported nothing.</param>
    /// <returns><c>0</c>, or the Win32 error that the framework would have thrown for.</returns>
    public static int Read(string path, out FileAttributes attributes)
    {
        attributes = default;

        var trimmed = Path.TrimEndingDirectorySeparator(path);

        var modeSet = SetThreadErrorMode(FailCriticalErrors, out var previousMode);

        try
        {
            if (GetFileAttributesEx(trimmed, GetFileExInfoStandard, out var data))
            {
                attributes = (FileAttributes)data.Attributes;
                return Success;
            }

            var error = Marshal.GetLastPInvokeError();

            if (IsUnreachable(error))
            {
                return error;
            }

            var handle = FindFirstFileEx(trimmed, FindExInfoBasic, out var found, FindExSearchNameMatch, 0, 0);

            if (handle == InvalidHandle)
            {
                return Marshal.GetLastPInvokeError();
            }

            FindClose(handle);

            attributes = (FileAttributes)found.Attributes;
            return Success;
        }
        finally
        {
            if (modeSet)
            {
                SetThreadErrorMode(previousMode, out _);
            }
        }
    }

    /// <summary>
    /// The errors after which the framework does not ask <c>FindFirstFileExW</c>, because they say
    /// the path cannot be reached at all. Its <c>IsPathUnreachableError</c>, copied.
    /// </summary>
    private static bool IsUnreachable(int win32Error) => win32Error is
        2 or      // ERROR_FILE_NOT_FOUND
        3 or      // ERROR_PATH_NOT_FOUND
        6 or      // ERROR_INVALID_HANDLE
        21 or     // ERROR_NOT_READY
        53 or     // ERROR_BAD_NETPATH
        65 or     // ERROR_NETWORK_ACCESS_DENIED
        67 or     // ERROR_BAD_NET_NAME
        87 or     // ERROR_INVALID_PARAMETER
        123 or    // ERROR_INVALID_NAME
        161 or    // ERROR_BAD_PATHNAME
        206 or    // ERROR_FILENAME_EXCED_RANGE
        1231;     // ERROR_NETWORK_UNREACHABLE

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeData
    {
        public uint Attributes;
        public FileTime Created;
        public FileTime Accessed;
        public FileTime Written;
        public uint SizeHigh;
        public uint SizeLow;
    }

    /// <summary>
    /// <c>WIN32_FIND_DATAW</c>. Only the attributes are read, but the call writes all 592 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FindData
    {
        public uint Attributes;
        public FileTime Created;
        public FileTime Accessed;
        public FileTime Written;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Reserved0;
        public uint Reserved1;
        public FileName Name;
        public AlternateFileName AlternateName;
    }

    /// <summary>
    /// Two halves rather than a <see cref="long"/>, which would align to eight bytes and move every
    /// field after the attributes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    /// <summary>
    /// UTF-16 units as <see cref="ushort"/>, because <see cref="char"/> is not blittable to the
    /// generated marshaller.
    /// </summary>
    [InlineArray(260)]
    private struct FileName
    {
        private ushort _first;
    }

    [InlineArray(14)]
    private struct AlternateFileName
    {
        private ushort _first;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileAttributesEx(string fileName, int infoLevel, out AttributeData data);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstFileEx(
        string fileName,
        int infoLevel,
        out FindData data,
        int searchOp,
        nint searchFilter,
        uint additionalFlags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint handle);

    /// <summary>
    /// Not <c>SetLastError</c>, so that restoring the mode cannot overwrite the error of the read it
    /// surrounds.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);
}
