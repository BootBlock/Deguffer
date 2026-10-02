using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Testing;

/// <summary>
/// Create a junction, as <c>mklink /J</c> does, and prove this machine will follow it before a test
/// relies on it, for the reason <see cref="FollowedLink"/> gives.
///
/// <para>.NET makes only symbolic links, so this writes the mount-point reparse data itself: an empty
/// folder, opened as the reparse point rather than through it, is given a substitute name in the
/// object manager's <c>\??\</c> form, which is what Windows follows, and a print name, which is what
/// a listing shows.</para>
/// </summary>
public static class Junction
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint MountPointTag = 0xA000_0003;

    /// <summary>The tag, the data length and a reserved word, ahead of the mount-point fields.</summary>
    private const int HeaderBytes = 8;

    /// <summary>The four offsets and lengths that place the two names in the path buffer.</summary>
    private const int NameFieldBytes = 8;

    public static void ToDirectory(string link, string target)
    {
        var extendedLink = LongPath.Extended(link);

        // CreateDirectory succeeds on a folder that is already there, and Windows makes a junction of any
        // empty one, so a reused folder would be replaced, and deleted by the clean-up below on a
        // failure. A symbolic link refuses an existing name, so this does.
        if (Path.Exists(extendedLink))
        {
            throw new IOException($"Cannot create the junction {link}: something already has that name.");
        }

        Directory.CreateDirectory(extendedLink);

        try
        {
            SetMountPoint(extendedLink, Path.GetFullPath(target));
        }
        catch
        {
            Directory.Delete(extendedLink);
            throw;
        }

        FollowedLink.Prove(new DirectoryInfo(link), "junction");
    }

    private static void SetMountPoint(string extendedLink, string fullTarget)
    {
        // The object manager's form is the extended-length form under its other prefix, a share's
        // UNC segment included, so both names come from LongPath rather than from the caller's spelling.
        var substitute = @"\??\" + LongPath.Extended(fullTarget)[@"\\?\".Length..];
        var print = LongPath.Display(fullTarget);
        var buffer = MountPointBuffer(substitute, print);

        using var handle = CreateFile(
            extendedLink, GenericWrite, 0, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {extendedLink} to make it a junction.");
        }

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not make {extendedLink} a junction to {print}.");
        }
    }

    /// <summary>
    /// <c>REPARSE_DATA_BUFFER</c> for a mount point. Each name is followed by a null that its length
    /// leaves out, the layout <c>mklink /J</c> writes.
    /// </summary>
    private static byte[] MountPointBuffer(string substitute, string print)
    {
        var substituteBytes = Encoding.Unicode.GetBytes(substitute + '\0');
        var printBytes = Encoding.Unicode.GetBytes(print + '\0');
        var dataLength = NameFieldBytes + substituteBytes.Length + printBytes.Length;
        var buffer = new byte[HeaderBytes + dataLength];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], checked((ushort)(substituteBytes.Length - sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], checked((ushort)(printBytes.Length - sizeof(char))));
        substituteBytes.CopyTo(span[16..]);
        printBytes.CopyTo(span[(16 + substituteBytes.Length)..]);

        return buffer;
    }

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
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inBuffer,
        int inBufferSize,
        nint outBuffer,
        int outBufferSize,
        out int bytesReturned,
        nint overlapped);
}
