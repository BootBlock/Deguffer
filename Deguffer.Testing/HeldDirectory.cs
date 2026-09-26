using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// A directory held open the way a program working in it holds it, for the length of a
/// <c>using</c> block. Windows' own refusal to remove a folder, rather than a fake's.
///
/// <para>A process's working directory is a handle to the folder that does not share deletion, so
/// Windows will not remove the folder while that process sits in it. This opens the same kind of
/// handle in the test's own process. That gives the same refusal without starting a program, and
/// without moving the test host's own working directory, which every test in the run shares.</para>
/// </summary>
public sealed class HeldDirectory : IDisposable
{
    private const uint Traverse = 0x0020;
    private const uint Synchronize = 0x0010_0000;
    private const uint ShareReadWrite = 0x0003;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly SafeFileHandle _handle;

    public HeldDirectory(string path)
    {
        _handle = CreateFile(
            LongPath.Extended(path), Traverse | Synchronize, ShareReadWrite, 0, OpenExisting, BackupSemantics, 0);

        if (_handle.IsInvalid)
        {
            throw new IOException($"Could not hold '{path}' open.", Marshal.GetHRForLastWin32Error());
        }

        try
        {
            // A handle that let the removal through would make every assertion downstream pass for
            // the wrong reason, so the refusal is proved here, in the form a working directory gives.
            var refusal = Assert.Throws<IOException>(() => Directory.Delete(LongPath.Extended(path)));
            Assert.Equal(SharingViolation, refusal.HResult);
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
}
