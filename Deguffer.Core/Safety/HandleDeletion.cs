using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Deletes the file a handle is open on, whatever its path names by then: what was opened, and
/// checked through that handle, is what goes, and nothing put at the path since.
///
/// <para>The handle must have been opened by path with delete access
/// (<see cref="FileInformation.OpenHeld"/>): NTFS refuses a disposition set through a handle
/// opened by number.</para>
/// </summary>
internal static partial class HandleDeletion
{
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;

    /// <summary>
    /// <c>FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
    /// FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE</c>: the name goes as the handle closes, even
    /// where another program holds the file open with deleting shared, and a read-only file goes
    /// without its attributes being changed first, which would change the file that was compared.
    /// </summary>
    private const uint DeleteNow = 0x0001 | 0x0002 | 0x0010;

    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;

    /// <summary>
    /// Marks the file <paramref name="handle"/> is open on for deletion, so it goes as the handle is
    /// closed, or answers the Win32 error Windows gave. Zero where it is marked.
    ///
    /// <para>A file system without POSIX deletion, such as FAT, refuses the extended call, so the
    /// older one is asked there. It deletes once every handle on the file is closed, still the file
    /// that was opened, and refuses a read-only file.</para>
    /// </summary>
    public static int Delete(SafeFileHandle handle)
    {
        var flags = DeleteNow;

        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref flags, sizeof(uint)))
        {
            return 0;
        }

        var error = Marshal.GetLastPInvokeError();

        if (error is not (ErrorInvalidParameter or ErrorNotSupported))
        {
            return error;
        }

        byte delete = 1;

        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref delete, sizeof(byte))
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, int size);
}
