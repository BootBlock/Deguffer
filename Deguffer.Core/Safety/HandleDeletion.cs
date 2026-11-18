using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// Deletes the entry a handle is open on, whatever its path names by then: what was opened, and
/// checked through that handle, is what goes, and nothing put at the path since.
///
/// <para>The handle must have been opened by path with delete access, as
/// <see cref="FileInformation.OpenHeld"/> and <see cref="EntryDeletion.Open"/> open one: NTFS
/// refuses a disposition set through a handle opened by number.</para>
/// </summary>
internal static partial class HandleDeletion
{
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;

    /// <summary>
    /// <c>FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
    /// FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE</c>: the name goes as the handle closes, even
    /// where another program holds the entry open with deleting shared, and a read-only entry goes
    /// without its attributes being changed first.
    /// </summary>
    private const uint DeleteNowWhateverItsBit = 0x0001 | 0x0002 | 0x0010;

    /// <summary>
    /// Marks the file <paramref name="handle"/> is open on for deletion, so it goes as the handle is
    /// closed, or answers the Win32 error Windows gave. Zero where it is marked.
    ///
    /// <para>A file system without the extended call (<see cref="IsUnsupported"/>), such as FAT, is
    /// asked the older one. It deletes once every handle on the file is closed, still the file that
    /// was opened, and refuses a read-only file.</para>
    /// </summary>
    public static int Delete(SafeFileHandle handle)
    {
        var error = TryDeleteNow(handle);

        if (!IsUnsupported(error))
        {
            return error;
        }

        byte delete = 1;

        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref delete, sizeof(byte))
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Marks the entry <paramref name="handle"/> is open on for deletion with the extended call alone,
    /// read-only or not, or answers the Win32 error Windows gave. Zero where it is marked. An error
    /// <see cref="IsUnsupported"/> recognises says the file system does not have the call, and nothing
    /// was asked of the entry.
    /// </summary>
    public static int TryDeleteNow(SafeFileHandle handle)
    {
        var flags = DeleteNowWhateverItsBit;

        return SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref flags, sizeof(uint))
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Whether <paramref name="error"/> is a file system saying it has no extended disposition, or no
    /// flag it was given: an invalid parameter or information class, a call it does not support, or
    /// one it does not implement, as a user-mode file system or a network redirector may answer.
    /// </summary>
    public static bool IsUnsupported(int error) =>
        error is Win32Error.InvalidParameter or Win32Error.NotSupported or Win32Error.InvalidFunction;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, int size);
}
