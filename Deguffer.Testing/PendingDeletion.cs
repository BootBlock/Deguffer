using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// A file whose deletion has been requested and not yet carried out, for the length of a
/// <c>using</c> block: what a program that deletes a file it still has open leaves behind.
///
/// <para>Its name stays in the directory until the handle closes, and <c>GetFileAttributesW</c>
/// refuses it with <c>ERROR_ACCESS_DENIED</c> while the directory's own index still describes it.
/// That makes it the one ordinary file that can prove an attribute read falls back to
/// <c>FindFirstFileExW</c>, as the framework's does. The deletion is requested with the legacy
/// disposition rather than the POSIX one <c>File.Delete</c> uses, which removes the name at
/// once.</para>
/// </summary>
public sealed class PendingDeletion : IDisposable
{
    private const uint Delete = 0x0001_0000;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfo = 4;
    private const uint InvalidFileAttributes = uint.MaxValue;
    private const int AccessDenied = 5;

    private readonly SafeFileHandle _handle;

    public PendingDeletion(string path)
    {
        var extended = LongPath.Extended(path);

        _handle = CreateFile(extended, Delete, ShareAll, 0, OpenExisting, 0, 0);

        if (_handle.IsInvalid)
        {
            throw new IOException($"Could not open '{path}' for deletion.", Marshal.GetHRForLastWin32Error());
        }

        try
        {
            var deleteFile = (byte)1;

            if (!SetFileInformationByHandle(_handle, FileDispositionInfo, ref deleteFile, 1))
            {
                throw new IOException($"Could not mark '{path}' for deletion.", Marshal.GetHRForLastWin32Error());
            }

            // The premise, proved rather than assumed: a first attribute read that answered would
            // leave the fallback unexercised, and a test over this would pass for the wrong reason.
            Assert.Equal(InvalidFileAttributes, GetFileAttributes(extended));
            Assert.Equal(AccessDenied, Marshal.GetLastPInvokeError());
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int informationClass, ref byte information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);
}
