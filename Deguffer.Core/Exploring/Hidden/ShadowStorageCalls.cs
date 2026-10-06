using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Hidden;

/// <summary>
/// The Volume Shadow Copy management calls that report shadow copy storage, from the SDK's
/// <c>vsmgmt.h</c>: the route <c>vssadmin list shadowstorage</c> takes.
///
/// <para>Chosen over <c>Win32_ShadowStorage</c>, which Microsoft keeps only in its archived
/// documentation, would have been Core's first package, and refuses an unelevated caller with an
/// "initialization failure" that cannot be told from any other. This interface refuses with
/// <c>E_ACCESSDENIED</c>, which is the one answer the page acts on.</para>
///
/// <para><b>Slot order is the header's.</b> A <c>ComImport</c> interface is a vtable, so every slot
/// before the one called is declared, in <c>vsmgmt.h</c>'s order, even where it is never
/// called.</para>
/// </summary>
internal static class ShadowStorageCalls
{
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// <c>VSS_E_OBJECT_NOT_FOUND</c>: the volume holds no shadow copy storage, or is not one the
    /// system provider keeps it for.
    /// </summary>
    public const int ObjectNotFound = unchecked((int)0x80042308);

    private const int S_OK = 0;

    private const uint LocalServer = 0x4;

    /// <summary><c>VSS_MGMT_OBJECT_TYPE</c>'s <c>VSS_MGMT_OBJECT_DIFF_AREA</c>.</summary>
    private const int DiffAreaObject = 3;

    /// <summary>
    /// <c>VSS_MGMT_OBJECT_PROP</c> is a four-byte type and a union whose members hold 64-bit fields,
    /// so the union starts eight bytes in on every architecture this app is built for.
    /// </summary>
    private const int UnionOffset = 8;

    /// <summary>Room for the largest union member on any architecture, with some to spare.</summary>
    private const int PropertySize = 64;

    private static readonly Guid SnapshotManagement = new("0B5A2C52-3EB9-470a-96E2-6C6D4570E40F");

    private static readonly Guid SnapshotManagementInterface = new("FA7DF749-66E7-4986-A27F-E2F04AE53772");

    /// <summary>
    /// <c>VSS_SWPRV_ProviderId</c>, the system provider. <c>GetProviderMgmtInterface</c> takes no
    /// other.
    /// </summary>
    private static readonly Guid SystemProvider = new("b5946137-7b9f-4925-af80-51abd60b20d5");

    private static readonly Guid DifferentialManagementInterface = new("214A0F28-B737-4026-B847-4F9E37D79529");

    /// <summary>
    /// The shadow copy storage on <paramref name="volumeRoot"/>, summed over every volume it serves.
    /// </summary>
    /// <param name="volumeRoot">A mount point ending in a separator, which the call requires.</param>
    public static ShadowStorage On(string volumeRoot)
    {
        var classId = SnapshotManagement;
        var interfaceId = SnapshotManagementInterface;
        var hr = CoCreateInstance(ref classId, IntPtr.Zero, LocalServer, ref interfaceId, out var created);

        if (hr != S_OK)
        {
            return Unanswered(hr);
        }

        try
        {
            var management = (IVssSnapshotMgmt)created;
            var providerId = SystemProvider;
            var differentialId = DifferentialManagementInterface;
            hr = management.GetProviderMgmtInterface(providerId, ref differentialId, out var provider);

            if (hr != S_OK)
            {
                return Unanswered(hr);
            }

            try
            {
                hr = ((IVssDifferentialSoftwareSnapshotMgmt)provider).QueryDiffAreasOnVolume(
                    volumeRoot, out var areas);

                if (hr == ObjectNotFound)
                {
                    return new ShadowStorage(Statement.Stated, MaximumBytes: 0);
                }

                if (hr != S_OK)
                {
                    return Unanswered(hr);
                }

                try
                {
                    return Summed(areas);
                }
                finally
                {
                    Marshal.ReleaseComObject(areas);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(provider);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(created);
        }
    }

    /// <summary>
    /// What a failure says. A refusal is the only one scanning as administrator would change, and
    /// every other is a figure Windows did not give.
    /// </summary>
    private static ShadowStorage Unanswered(int hr) =>
        hr == AccessDenied ? ShadowStorage.Refused : new ShadowStorage(Statement.NotStated);

    /// <summary>
    /// Every storage area the enumeration holds, added together. An enumeration that stops answering
    /// part way is a figure Windows did not finish giving, so it states nothing rather than a
    /// partial sum.
    /// </summary>
    private static ShadowStorage Summed(IVssEnumMgmtObject areas)
    {
        long used = 0;
        long allocated = 0;
        long? maximum = 0;
        var property = Marshal.AllocCoTaskMem(PropertySize);

        try
        {
            while (true)
            {
                var hr = areas.Next(1, property, out var fetched);

                if (hr < 0)
                {
                    return new ShadowStorage(Statement.NotStated);
                }

                if (fetched == 0)
                {
                    break;
                }

                var (type, area) = Read(property);

                if (type != DiffAreaObject)
                {
                    continue;
                }

                used += area.Used;
                allocated += area.Allocated;

                // VSS_ASSOC_NO_MAX_SPACE, -1, is an area allowed to grow without limit, and one such
                // area makes the volume's limit none.
                maximum = maximum is { } sum && area.Maximum >= 0 ? sum + area.Maximum : null;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(property);
        }

        return new ShadowStorage(Statement.Stated, used, allocated, maximum);
    }

    /// <summary>
    /// One <c>VSS_MGMT_OBJECT_PROP</c>, read by offset, freeing the two strings the service
    /// allocated in it.
    ///
    /// <para>By offset rather than as a marshalled struct, because the union's two pointers are four
    /// bytes on x86 and eight elsewhere, and the 64-bit fields after them align to eight on both. A
    /// sequential struct describes one of those layouts and silently misreads the other.</para>
    /// </summary>
    private static (int Type, DiffArea Area) Read(IntPtr property)
    {
        var type = Marshal.ReadInt32(property);
        var volumeName = Marshal.ReadIntPtr(property, UnionOffset);
        var diffAreaVolumeName = Marshal.ReadIntPtr(property, UnionOffset + IntPtr.Size);

        // Both strings are the caller's to free whatever the object turned out to be: every union
        // member opens with the same two.
        Marshal.FreeCoTaskMem(volumeName);
        Marshal.FreeCoTaskMem(diffAreaVolumeName);

        var figures = UnionOffset + (2 * IntPtr.Size);
        figures = (figures + 7) & ~7;

        return (type, new DiffArea(
            Marshal.ReadInt64(property, figures),
            Marshal.ReadInt64(property, figures + 8),
            Marshal.ReadInt64(property, figures + 16)));
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    /// <summary><c>VSS_DIFF_AREA_PROP</c>'s three figures, in its order.</summary>
    private readonly record struct DiffArea(long Maximum, long Allocated, long Used);
}

[ComImport]
[Guid("FA7DF749-66E7-4986-A27F-E2F04AE53772")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssSnapshotMgmt
{
    [PreserveSig]
    int GetProviderMgmtInterface(
        Guid providerId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object provider);
}

[ComImport]
[Guid("214A0F28-B737-4026-B847-4F9E37D79529")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssDifferentialSoftwareSnapshotMgmt
{
    [PreserveSig]
    int AddDiffArea(IntPtr volumeName, IntPtr diffAreaVolumeName, long maximumDiffSpace);

    [PreserveSig]
    int ChangeDiffAreaMaximumSize(IntPtr volumeName, IntPtr diffAreaVolumeName, long maximumDiffSpace);

    [PreserveSig]
    int QueryVolumesSupportedForDiffAreas(IntPtr originalVolumeName, out IntPtr enumeration);

    [PreserveSig]
    int QueryDiffAreasForVolume(IntPtr volumeName, out IntPtr enumeration);

    /// <summary>The storage areas that are physically on the volume, whichever volumes they serve.</summary>
    [PreserveSig]
    int QueryDiffAreasOnVolume(
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        out IVssEnumMgmtObject enumeration);
}

[ComImport]
[Guid("01954E6B-9254-4e6e-808C-C9E05D007696")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssEnumMgmtObject
{
    /// <summary>
    /// Copies up to <paramref name="count"/> objects into <paramref name="properties"/>.
    /// <c>S_FALSE</c> with none fetched is the end.
    /// </summary>
    [PreserveSig]
    int Next(uint count, IntPtr properties, out uint fetched);
}
