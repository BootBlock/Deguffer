using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// <c>NtQuerySystemInformation</c>, declared once for the two readers that ask it different
/// questions, and a buffer the kernel can write addresses into.
/// </summary>
internal static partial class SystemInformation
{
    public const int ProcessInformation = 5;
    public const int MemoryListInformation = 80;

    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// An array that never moves, so the address the kernel is handed stays the address of the array
    /// after the call returns, and the pointers it wrote into the array can be translated back into
    /// positions in it. Allocated on the pinned object heap rather than pinned per call.
    /// </summary>
    public static T[] PinnedArray<T>(int length) where T : unmanaged =>
        GC.AllocateUninitializedArray<T>(length, pinned: true);

    public static nint AddressOf<T>(T[] pinned) where T : unmanaged =>
        Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);

    public static Win32Exception Failure(int status) => new(RtlNtStatusToDosError(status));

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returned);

    [LibraryImport("ntdll.dll")]
    private static partial int RtlNtStatusToDosError(int status);
}
