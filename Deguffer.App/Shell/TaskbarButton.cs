using System.Runtime.InteropServices;
using Deguffer.Core.Working;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Shell;

/// <summary>
/// Writes what <see cref="TaskbarIndicator"/> decides onto the main window's taskbar button: the
/// progress bar through <c>ITaskbarList3</c>, and a flash through <c>FlashWindowEx</c>.
///
/// <para>The button is decoration, as the backdrop is (§6.5), so a taskbar that refuses is left as
/// it is. Explorer may not be the shell, or may be restarting. A write that fails returns its
/// HRESULT rather than throwing, and the next one, or the button being made again, puts it right.
/// A taskbar that could not be reached when the window opened is asked for again when Explorer says
/// it has made the button.</para>
///
/// <para>Explorer forgets every button's bar when it restarts, and says so with the registered
/// <c>TaskbarButtonCreated</c> message once it has made the button again, so the bar is written
/// again then. An elevated window does not hear that message from a shell running unelevated unless
/// it lets it through, and Deguffer relaunches elevated, so it is let through.</para>
/// </summary>
internal sealed class TaskbarButton
{
    /// <summary>Which of the window's subclasses this is, among any others comctl32 holds for it.</summary>
    private const nuint SubclassId = 0x544252;

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;
    private const uint AllowMessage = 1;

    private static readonly uint TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

    private readonly nint _window;
    private readonly TaskbarIndicator _indicator;
    private ITaskbarList3? _taskbar;

    /// <summary>What the OS holds a raw pointer to, rooted for the window's life as SystemMotion's is.</summary>
    private readonly SubclassProc _watch;

    /// <summary>Show <paramref name="indicator"/> on <paramref name="window"/>'s taskbar button for the window's life.</summary>
    public TaskbarButton(Window window, TaskbarIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(indicator);

        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _indicator = indicator;
        _taskbar = Connect();
        _watch = OnMessage;

        ChangeWindowMessageFilterEx(_window, TaskbarButtonCreated, AllowMessage, 0);
        SetWindowSubclass(_window, _watch, SubclassId, 0);

        indicator.Changed += (_, _) => Show();
        indicator.AttentionWanted += (_, _) => Flash();
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _indicator.Seen();
            }
        };

        Show();
    }

    /// <summary>
    /// Whether the window the user is working in belongs to this process: the main window, or one of
    /// the app's own tool windows.
    /// </summary>
    public static bool UserIsHere()
    {
        var foreground = GetForegroundWindow();

        return foreground != 0
            && GetWindowThreadProcessId(foreground, out var process) != 0
            && process == (uint)Environment.ProcessId;
    }

    private static ITaskbarList3? Connect()
    {
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarList();

            return taskbar.HrInit() >= 0 ? taskbar : null;
        }
        catch (COMException)
        {
            // No taskbar to write to: a shell other than Explorer, or none. The bar is decoration.
            return null;
        }
    }

    private void Show()
    {
        if (_taskbar is null)
        {
            return;
        }

        var progress = _indicator.Current;

        _taskbar.SetProgressState(_window, progress.State switch
        {
            TaskbarState.Indeterminate => ProgressFlags.Indeterminate,
            TaskbarState.Normal => ProgressFlags.Normal,
            TaskbarState.Error => ProgressFlags.Error,
            _ => ProgressFlags.NoProgress,
        });

        // After the state, because setting a value while the bar is indeterminate turns it normal.
        if (progress.State is TaskbarState.Normal or TaskbarState.Error)
        {
            _taskbar.SetProgressValue(_window, (ulong)progress.Thousandths, TaskbarProgress.Whole);
        }
    }

    private void Flash()
    {
        var flash = new FlashInformation
        {
            Size = (uint)Marshal.SizeOf<FlashInformation>(),
            Window = _window,
            Flags = FlashTray | FlashUntilForeground,
        };

        FlashWindowEx(ref flash);
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == TaskbarButtonCreated)
        {
            _taskbar ??= Connect();
            Show();
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private enum ProgressFlags
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInformation
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    /// <summary>
    /// <c>ITaskbarList3</c> as far as its progress calls, with <c>ITaskbarList</c> and
    /// <c>ITaskbarList2</c> before them, in shobjidl_core.h's order: a <c>ComImport</c> interface is a
    /// vtable, and a slot out of place calls the wrong method with no error (see Direct3D).
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint window);

        [PreserveSig]
        int DeleteTab(nint window);

        [PreserveSig]
        int ActivateTab(nint window);

        [PreserveSig]
        int SetActiveAlt(nint window);

        [PreserveSig]
        int MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        [PreserveSig]
        int SetProgressValue(nint window, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(nint window, ProgressFlags flags);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);

    // DllImport rather than LibraryImport, matching HighContrast: the generator wants AllowUnsafeBlocks
    // across the whole project, which is a large blast radius for these calls.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint filterStatus);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInformation information);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
