using System.ComponentModel;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// Make a folder case-sensitive, as <c>fsutil file setCaseSensitiveInfo</c> does, so two files in it
/// can differ only in the case of a letter. NTFS allows it on an empty folder, without elevation.
/// </summary>
public static class CaseSensitiveFolder
{
    private const int FileCaseSensitiveInfo = 23;
    private const uint CaseSensitiveDirectory = 1;
    private const uint FileWriteAttributes = 0x0100;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;

    /// <summary>Create <paramref name="folder"/>, empty, and make it case-sensitive.</summary>
    /// <returns><paramref name="folder"/>.</returns>
    public static string Create(string folder)
    {
        Directory.CreateDirectory(folder);

        using var handle = CreateFile(LongPath.Extended(folder), FileWriteAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        var flags = CaseSensitiveDirectory;

        if (handle.IsInvalid || !SetFileInformationByHandle(handle, FileCaseSensitiveInfo, ref flags, sizeof(uint)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not make {folder} case-sensitive.");
        }

        return folder;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, nint securityAttributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, int size);
}
