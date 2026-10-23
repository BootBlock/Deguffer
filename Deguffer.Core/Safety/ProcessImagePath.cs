using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// The full path of the program a process runs, read through a handle the caller holds. One reading
/// for every caller that asks, the live-tree check and Memory's facts among them, so the two never
/// disagree about a long path.
/// </summary>
internal static partial class ProcessImagePath
{
    /// <summary>The longest path Windows has, so no image is cut short and read as unanswered.</summary>
    private const int Characters = 32_768;

    /// <summary>
    /// The path in the Win32 form, which compares with a path a registry value or a shortcut names,
    /// or null where Windows would not say. Needs only <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
    /// </summary>
    public static string? Of(nint process)
    {
        var buffer = Marshal.AllocHGlobal(Characters * sizeof(char));

        try
        {
            var size = (uint)Characters;

            return QueryFullProcessImageName(process, 0, buffer, ref size) ? Marshal.PtrToStringUni(buffer, (int)size) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, nint buffer, ref uint size);
}
