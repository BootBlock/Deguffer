using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// A file marked sparse and given a length it has written nothing into, so it takes almost none of
/// the drive. It is what a sparse WSL disk looks like from outside, built in a test's own folder.
/// </summary>
public static class SparseFile
{
    private const uint SetSparse = 0x000900C4;

    public static void Create(string path, long length)
    {
        using (var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            if (!DeviceIoControl(handle, SetSparse, 0, 0, 0, 0, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not mark {path} sparse.");
            }
        }

        // Extending a sparse file allocates nothing for the new range.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.SetLength(length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
