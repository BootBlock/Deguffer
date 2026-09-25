using System.Runtime.InteropServices;

namespace Deguffer.App.Shell;

/// <summary>
/// Writes one of a window's Win32 attributes, on every architecture Deguffer ships for.
///
/// <para>32-bit user32 has no <c>SetWindowLongPtrW</c> — there it is a macro over
/// <c>SetWindowLongW</c>, and binding the Ptr name would fail to resolve at runtime. x86 is a
/// supported platform here (§6.3 ships per-architecture), so both are bound, once, for every
/// window that needs one.</para>
/// </summary>
internal static class WindowLong
{
    /// <summary>Set attribute <paramref name="index"/> of <paramref name="window"/>, returning the old value.</summary>
    public static nint Set(nint window, int index, nint value) => nint.Size == 8
        ? SetWindowLongPtr(window, index, value)
        : SetWindowLong(window, index, value.ToInt32());

    // DllImport rather than LibraryImport, matching HighContrast: the generator wants
    // AllowUnsafeBlocks across the whole project, which is a large blast radius for these calls.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong(nint hWnd, int index, int value);
}
