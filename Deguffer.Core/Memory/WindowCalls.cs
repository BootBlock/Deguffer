using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// The Windows calls <see cref="WindowSurveyor"/> makes about top-level windows, as a seam a test can
/// drive. This machine's desktop cannot be made to hold a cloaked window, a console window owned by a
/// program that is not a console program, or a window that is destroyed between two calls.
///
/// <para>Every member answers null where Windows would not say, and <see cref="Exists"/> is what tells
/// that apart from a window that has gone since it was enumerated.</para>
/// </summary>
internal interface IWindowCalls
{
    /// <summary>
    /// Every top-level window, in the order Windows enumerates them, or null where it would not
    /// enumerate them.
    /// </summary>
    IReadOnlyList<nint>? TopLevel();

    /// <summary>The process that created <paramref name="window"/>, or null where the window has gone.</summary>
    int? ProcessOf(nint window);

    /// <summary>The class Windows registered it under.</summary>
    string? ClassOf(nint window);

    /// <summary>Whether it has an owner, which a dialog and a message box have by default.</summary>
    bool? IsOwned(nint window);

    /// <summary>Whether it and every ancestor is visible. A window that has gone is not.</summary>
    bool IsVisible(nint window);

    /// <summary>
    /// Whether the compositor is holding it off the screen, which is how the shell holds a window on
    /// another virtual desktop. Any cloak reads as "not on screen", and the value is given no further
    /// meaning (§7.2.1).
    /// </summary>
    bool? IsCloaked(nint window);

    /// <summary>Whether it is still a window at all.</summary>
    bool Exists(nint window);

    /// <summary>
    /// The shell's own window, or null where Windows reports none. §7.2.1 refuses the process that
    /// owns it, because the shell is only "usually explorer.exe" and this names it exactly.
    /// </summary>
    nint? ShellWindow();

    /// <summary>
    /// Post <c>WM_CLOSE</c> to <paramref name="window"/>, and report whether Windows took it.
    ///
    /// <para><b>This is the only message any part of Deguffer sends a window, and the only member
    /// here that changes anything.</b> §7.2.1 has one verb: the program's own close, posted rather
    /// than sent, so a program already busy behind a modal dialog does not hold Deguffer there with
    /// it. What the return value means is deliberately not relied on for the decision — Microsoft's
    /// own sources disagree about whether a post the integrity filter blocks reports failure or
    /// reports success and drops the message — so the policy decides before this is called, and this
    /// answer only says what to record.</para>
    /// </summary>
    bool PostClose(nint window);
}

/// <inheritdoc />
internal sealed partial class WindowCalls : IWindowCalls
{
    public static readonly WindowCalls Instance = new();

    private const uint Owner = 4;
    private const int Cloaked = 14;

    /// <summary>
    /// <c>WM_CLOSE</c>, which Windows documents as the signal "that a window or an application should
    /// terminate", whose default handling destroys the window and which an application "can prompt
    /// the user for confirmation" about first. It is the whole of what §7.2.1 sends, and unsaved work
    /// is therefore the program's own question, asked in its own words.
    /// </summary>
    private const uint WindowClose = 0x0010;

    /// <summary>A window class name is at most 256 characters, and the terminator makes 257.</summary>
    private const int MaximumClassName = 257;

    private WindowCalls()
    {
    }

    /// <summary>
    /// The enumeration reports failure only through its own return value: this callback never asks it
    /// to stop, so a false return is Windows refusing rather than the walk ending early.
    /// </summary>
    public IReadOnlyList<nint>? TopLevel()
    {
        var windows = new List<nint>();

        return EnumWindows(
            (window, _) =>
            {
                windows.Add(window);
                return true;
            },
            0)
            ? windows
            : null;
    }

    public int? ProcessOf(nint window) =>
        GetWindowThreadProcessId(window, out var process) == 0 ? null : (int)process;

    public string? ClassOf(nint window)
    {
        var name = new char[MaximumClassName];
        var length = GetClassName(window, name, name.Length);

        return length == 0 ? null : new string(name, 0, length);
    }

    /// <summary>
    /// Windows answers nothing both for a window with no owner and for a call that failed, so the last
    /// error is what tells them apart.
    /// </summary>
    public bool? IsOwned(nint window)
    {
        Marshal.SetLastSystemError(0);
        var owner = GetWindow(window, Owner);

        return owner != 0 ? true
            : Marshal.GetLastPInvokeError() == 0 ? false
            : null;
    }

    public bool IsVisible(nint window) => IsWindowVisible(window);

    public bool? IsCloaked(nint window) =>
        DwmGetWindowAttribute(window, Cloaked, out var cloak, sizeof(int)) >= 0 ? cloak != 0 : null;

    public bool Exists(nint window) => IsWindow(window);

    public nint? ShellWindow() => GetShellWindow() is var shell && shell != 0 ? shell : null;

    /// <summary>
    /// <c>WM_CLOSE</c> and no parameters, written as constants rather than taken from a caller, so
    /// there is no message this type can be asked to send.
    /// </summary>
    public bool PostClose(nint window) => PostMessage(window, WindowClose, 0, 0);

    /// <summary>
    /// Declared with <c>DllImport</c>, unlike the rest of this file: the source generator cannot
    /// marshal a managed callback, and this is the documented way to enumerate top-level windows.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumerateWindows callback, nint parameter);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumerateWindows(nint window, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint window, [Out] char[] name, int length);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindow(nint window, uint relationship);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    /// <summary>
    /// The one call in Deguffer that puts a message in another program's queue. Its message argument
    /// is <see cref="WindowClose"/> at every call site, and there is exactly one.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int length);
}
