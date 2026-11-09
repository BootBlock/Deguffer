using System.Runtime.InteropServices;

namespace Deguffer.Core.Health;

/// <summary>
/// The disks Windows has an interface for, which is how the page finds them. Disk numbers can have
/// gaps, so counting up from <c>PhysicalDrive0</c> until one fails would stop short.
/// </summary>
internal static partial class DeviceInterfaces
{
    private const int Success = 0;
    private const int BufferSmall = 0x1A;
    private const uint PresentOnly = 0;

    /// <summary><c>GUID_DEVINTERFACE_DISK</c>.</summary>
    private static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    /// <summary>The path of every present disk's interface, or null where Windows would not list them.</summary>
    internal static IReadOnlyList<string>? Disks()
    {
        var guid = DiskInterface;

        // A disk attached between the two calls makes the first size too small, so it is asked again.
        while (true)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, ref guid, nint.Zero, PresentOnly) != Success)
            {
                return null;
            }

            var buffer = new char[length];
            var result = Fill(ref guid, buffer, length);

            if (result == Success)
            {
                return Split(buffer);
            }

            if (result != BufferSmall)
            {
                return null;
            }
        }
    }

    private static unsafe int Fill(ref Guid guid, char[] buffer, uint length)
    {
        fixed (char* pointer = buffer)
        {
            return CM_Get_Device_Interface_List(ref guid, nint.Zero, pointer, length, PresentOnly);
        }
    }

    /// <summary>A multi-string: each path ended by a null, and the set ended by an empty one.</summary>
    internal static IReadOnlyList<string> Split(ReadOnlySpan<char> multiString)
    {
        var paths = new List<string>();

        while (multiString.IndexOf('\0') is var end and > 0)
        {
            paths.Add(multiString[..end].ToString());
            multiString = multiString[(end + 1)..];
        }

        return paths;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial int CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static unsafe partial int CM_Get_Device_Interface_List(ref Guid interfaceClass, nint deviceId, char* buffer, uint length, uint flags);
}
