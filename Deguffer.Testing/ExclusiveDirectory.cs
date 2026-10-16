using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// A directory this process holds open to list, sharing nothing, for the length of a <c>using</c>
/// block: any other open that asks to list it meets a sharing violation at the open.
///
/// <para>Not <see cref="HeldDirectory"/>, which shares reading and writing as a working directory does,
/// so a listing still opens the folder. Windows enforces the sharing only against a handle that holds
/// data access, so this one asks to list the folder rather than only to traverse it.</para>
/// </summary>
public sealed class ExclusiveDirectory : IDisposable
{
    private const uint ListDirectory = 0x0001;
    private const uint ShareNothing = 0;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly SafeFileHandle _handle;

    public ExclusiveDirectory(string path)
    {
        _handle = CreateFile(LongPath.Extended(path), ListDirectory, ShareNothing, 0, OpenExisting, BackupSemantics, 0);

        if (_handle.IsInvalid)
        {
            throw new IOException($"Could not open '{path}' exclusively.", Marshal.GetHRForLastWin32Error());
        }

        try
        {
            // A handle another listing could share would let every assertion downstream pass for the
            // wrong reason, so the sharing violation is proved here.
            var refusal = Assert.Throws<IOException>(() => Directory.EnumerateFileSystemEntries(LongPath.Extended(path)).Any());
            Assert.Equal(SharingViolation, refusal.HResult);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void Dispose() => _handle.Dispose();

    // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks; this project does not set it.
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
