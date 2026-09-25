using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// The Disk Cleanup handler interfaces <see cref="DiskCleanupHandlers"/> hosts, from the SDK's
/// <c>EmptyVC.h</c>.
///
/// <para><b>Slot order is the header's, never the documentation's.</b> Microsoft Learn lists the
/// methods alphabetically, and a COM interface is a vtable: the runtime calls the slot at the declared
/// position, so a method declared out of order silently calls its neighbour. The interfaces are
/// declared here in <c>EmptyVC.h</c>'s order, and <see cref="IEmptyVolumeCache2"/> repeats its base's
/// five methods before its own, because a <c>ComImport</c> interface inherits no slots.</para>
///
/// <para>Declared with <c>PreserveSig</c>, unlike <see cref="ShellNative"/>'s interfaces. Two of the
/// answers that matter are success codes, <c>S_FALSE</c> among them, and an exception carries only the
/// failures.</para>
/// </summary>
internal static class DiskCleanupNative
{
    public const int S_OK = 0;

    /// <summary>From <c>Initialize</c>: the handler has nothing to delete on this volume.</summary>
    public const int S_FALSE = 1;

    public const int E_ABORT = unchecked((int)0x80004004);

    /// <summary>
    /// <c>EVCF_USERCONSENTOBTAINED</c>: the person has already agreed to the cleanup. Deguffer has
    /// shown what goes and asked the confirmation §7 requires before any handler runs.
    /// </summary>
    public const uint UserConsentObtained = 0x0080;

    public const uint InProcessServer = 0x1;

    public static readonly Guid VolumeCacheId = new("8FCE5227-04DA-11d1-A004-00805F8ABE06");

    public static readonly Guid VolumeCache2Id = new("02b7e3ba-4db3-11d2-b2d9-00c04f8eec8c");

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

/// <summary>
/// A Disk Cleanup handler, through the interface every handler implements. Asked for only where a
/// handler does not implement <see cref="IEmptyVolumeCache2"/>.
/// </summary>
[ComImport]
[Guid("8FCE5227-04DA-11d1-A004-00805F8ABE06")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCache
{
    [PreserveSig]
    int Initialize(
        IntPtr key,
        [MarshalAs(UnmanagedType.LPWStr)] string volume,
        out IntPtr displayName,
        out IntPtr description,
        ref uint flags);

    [PreserveSig]
    int GetSpaceUsed(out ulong spaceUsed, IEmptyVolumeCacheCallBack callback);

    [PreserveSig]
    int Purge(ulong spaceToFree, IEmptyVolumeCacheCallBack callback);

    [PreserveSig]
    int ShowProperties(IntPtr window);

    [PreserveSig]
    int Deactivate(out uint flags);
}

/// <summary>
/// A Disk Cleanup handler that is also told the name of the registration it was created for. Asked
/// for first, as Disk Cleanup asks. <c>setupcln.dll</c> was observed not to implement it, and tells
/// its six registrations apart by the values on the key it is handed instead.
/// </summary>
[ComImport]
[Guid("02b7e3ba-4db3-11d2-b2d9-00c04f8eec8c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCache2
{
    [PreserveSig]
    int Initialize(
        IntPtr key,
        [MarshalAs(UnmanagedType.LPWStr)] string volume,
        out IntPtr displayName,
        out IntPtr description,
        ref uint flags);

    [PreserveSig]
    int GetSpaceUsed(out ulong spaceUsed, IEmptyVolumeCacheCallBack callback);

    [PreserveSig]
    int Purge(ulong spaceToFree, IEmptyVolumeCacheCallBack callback);

    [PreserveSig]
    int ShowProperties(IntPtr window);

    [PreserveSig]
    int Deactivate(out uint flags);

    [PreserveSig]
    int InitializeEx(
        IntPtr key,
        [MarshalAs(UnmanagedType.LPWStr)] string volume,
        [MarshalAs(UnmanagedType.LPWStr)] string keyName,
        out IntPtr displayName,
        out IntPtr description,
        out IntPtr buttonText,
        ref uint flags);
}

/// <summary>What a handler reports its progress to, and the one way a host can ask it to stop.</summary>
[ComImport]
[Guid("6E793361-73C6-11D0-8469-00AA00442901")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEmptyVolumeCacheCallBack
{
    [PreserveSig]
    int ScanProgress(ulong spaceUsed, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string? status);

    [PreserveSig]
    int PurgeProgress(
        ulong spaceFreed,
        ulong spaceToFree,
        uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string? status);
}

/// <summary>
/// The progress sink handed to a handler. It reports nothing onward, and answers every report with
/// <c>E_ABORT</c> once the run is cancelled.
///
/// <para>A handler is not required to cope with a missing sink — the documentation describes it as
/// the Disk Cleanup manager's own interface, never as optional — so one is always passed.</para>
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CancellingCallback(CancellationToken ct) : IEmptyVolumeCacheCallBack
{
    public int ScanProgress(ulong spaceUsed, uint flags, string? status) => Answer();

    public int PurgeProgress(ulong spaceFreed, ulong spaceToFree, uint flags, string? status) => Answer();

    private int Answer() => ct.IsCancellationRequested ? DiskCleanupNative.E_ABORT : DiskCleanupNative.S_OK;
}
