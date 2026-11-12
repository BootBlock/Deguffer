using System.Runtime.InteropServices;
using Deguffer.Core.Native;

namespace Deguffer.Core.Safety;

/// <inheritdoc />
/// <remarks>
/// <para><b>Read from the configuration manager in process.</b> On a machine with 252 devices,
/// unelevated, it took 12 to 23 ms, against about a second of processor time for pnputil to list each
/// package's devices, and named the same 31 packages in use, 3 of them extensions.</para>
///
/// <para><b>Phantoms included.</b> pnputil counts a package as in use by a device that is not
/// connected, so this lists every device the configuration manager knows, and locates each one
/// whether it is present or not.</para>
/// </remarks>
public sealed partial class DeviceDriverReader : IDeviceDriverReader
{
    private const int Success = 0;
    private const int BufferSmall = 0x1A;
    private const int NoSuchValue = 0x25;

    /// <summary><c>CM_GETIDLIST_FILTER_NONE</c>: every device, present or not.</summary>
    private const uint EveryDevice = 0;

    /// <summary><c>CM_LOCATE_DEVNODE_PHANTOM</c>: a device that is not present is located too.</summary>
    private const uint EvenAPhantom = 1;

    private const uint StringType = 0x12;
    private const uint StringListType = 0x2012;

    private static readonly PropertyKey InfPath = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 5);
    private static readonly PropertyKey ExtendedConfigurationIds = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 15);

    private DeviceDriverReader()
    {
    }

    public static DeviceDriverReader Default { get; } = new();

    public IReadOnlyList<DeviceDriverProperties>? Read(CancellationToken ct)
    {
        if (DeviceIds() is not { } ids)
        {
            return null;
        }

        var devices = new List<DeviceDriverProperties>(ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            // A device removed since the list was taken fails to locate, and fails the reading with it.
            if (CM_Locate_DevNode(out var node, id, EvenAPhantom) != Success
                || Strings(node, InfPath, StringType) is not { } inf
                || Strings(node, ExtendedConfigurationIds, StringListType) is not { } extensions)
            {
                return null;
            }

            devices.Add(new DeviceDriverProperties(inf.Count > 0 ? inf[0] : null, extensions));
        }

        return devices;
    }

    private static IReadOnlyList<string>? DeviceIds()
    {
        // A device arriving between the two calls makes the first size too small, so it is asked again.
        while (true)
        {
            if (CM_Get_Device_ID_List_Size(out var length, null, EveryDevice) != Success)
            {
                return null;
            }

            var buffer = new char[length];
            var result = FillIds(buffer, length);

            if (result == Success)
            {
                return MultiString.Split(buffer);
            }

            if (result != BufferSmall)
            {
                return null;
            }
        }
    }

    private static unsafe int FillIds(char[] buffer, uint length)
    {
        fixed (char* pointer = buffer)
        {
            return CM_Get_Device_ID_List(null, pointer, length, EveryDevice);
        }
    }

    /// <summary>
    /// A string or string-list property: empty where the device has no value, null where it could not
    /// be read or holds some other type.
    /// </summary>
    private static unsafe IReadOnlyList<string>? Strings(uint node, PropertyKey key, uint expected)
    {
        byte[]? buffer = null;
        uint size = 0;

        while (true)
        {
            int result;
            uint type;

            fixed (byte* pointer = buffer)
            {
                result = CM_Get_DevNode_Property(node, in key, out type, pointer, ref size, 0);
            }

            switch (result)
            {
                case NoSuchValue:
                    return [];
                case BufferSmall:
                    buffer = new byte[size];
                    continue;
                case Success when type == expected:
                    return buffer is null ? [] : MultiString.Split(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)size)));
                default:
                    return null;
            }
        }
    }

    /// <summary><c>DEVPROPKEY</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid Format, uint Id);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int CM_Get_Device_ID_List(string? filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNode(out uint node, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static unsafe partial int CM_Get_DevNode_Property(uint node, in PropertyKey key, out uint type, byte* buffer, ref uint size, uint flags);
}
