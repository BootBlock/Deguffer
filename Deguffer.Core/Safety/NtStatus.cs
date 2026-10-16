using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// <c>RtlNtStatusToDosError</c>, declared once: the Win32 error a native call's <c>NTSTATUS</c> means,
/// as the Win32 call built on it would have set it.
/// </summary>
internal static partial class NtStatus
{
    [LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
    public static partial int ToWin32Error(int status);
}
