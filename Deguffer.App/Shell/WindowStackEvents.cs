using System.Runtime.InteropServices;

namespace Deguffer.App.Shell;

/// <summary>
/// Any top-level window on the desktop moved, resized, came to the front, was shown, hidden, minimised,
/// restored, cloaked or uncloaked: anything that can change what of Deguffer's windows other windows
/// cover. Heard through Windows' accessibility events, which tell every process of every window.
///
/// <para>Hooked only while something listens, and unhooked when the last listener goes, because the
/// location event is raised for every pointer move on the desktop as well, and each one is a message
/// to the UI thread before it can be filtered out. Hooked out of context, so Windows posts each event
/// to the thread that hooked it, which is the UI thread every listener subscribes on.</para>
/// </summary>
internal sealed class WindowStackEvents
{
    public static WindowStackEvents Current { get; } = new();

    private const uint OutOfContext = 0;
    private const uint Reorder = 0x8004;
    private const int WindowObject = 0;
    private const int Self = 0;
    private const uint Root = 2;

    /// <summary>
    /// The ranges of events hooked: a window brought to the front; minimised and restored; shown,
    /// hidden and reordered; moved or resized; and cloaked and uncloaked. A window destroyed is hidden
    /// first, so its destruction is not hooked: <see cref="Concerns"/> takes the hiding of a window
    /// that has gone since as a change.
    /// </summary>
    private static readonly (uint First, uint Last)[] Ranges =
    [
        (0x0003, 0x0003),
        (0x0016, 0x0017),
        (0x8002, 0x8004),
        (0x800B, 0x800B),
        (0x8017, 0x8018),
    ];

    /// <summary>
    /// What Windows holds a raw pointer to. Collected while hooked, the next event would crash the
    /// process, so it is rooted here, for the process's life.
    /// </summary>
    private readonly WinEventProc _callback;

    private readonly List<nint> _hooks = [];
    private EventHandler? _changed;

    private WindowStackEvents() => _callback = OnEvent;

    /// <summary>
    /// A window moved in a way that can change what is covered. Raised on the UI thread, once for each
    /// event, so a listener throttles what it does with it.
    /// </summary>
    public event EventHandler? Changed
    {
        add
        {
            var first = _changed is null;
            _changed += value;

            if (first && _changed is not null)
            {
                Hook();
            }
        }

        remove
        {
            _changed -= value;

            if (_changed is null)
            {
                Unhook();
            }
        }
    }

    /// <summary>
    /// A range Windows refuses to hook is left unheard, and a listener goes on with what it last
    /// decided: that costs at most the processor time this is here to save, never a fault anyone sees.
    /// </summary>
    private void Hook()
    {
        foreach (var (first, last) in Ranges)
        {
            if (SetWinEventHook(first, last, 0, _callback, 0, 0, OutOfContext) is var hook and not 0)
            {
                _hooks.Add(hook);
            }
        }
    }

    private void Unhook()
    {
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    /// <summary>
    /// Whether a window's own event can change what is covered: a top-level window's, or the desktop's
    /// when it reorders the windows on it.
    ///
    /// <para>A window that has gone counts as well. Each event is posted to the UI thread after it was
    /// raised, and a window that closes is hidden and destroyed in one call in its own process, so by
    /// the time its hiding is read its handle is usually gone and nothing says whether it was top-level.
    /// Dropping it would leave a field paused behind a window that closed by itself, a dialog that
    /// finished or a program ended from Task Manager, in front of the reader until anything else on
    /// the desktop moved. Taking it costs one more look.</para>
    /// </summary>
    internal static bool Concerns(uint kind, EventWindow window) => window switch
    {
        EventWindow.TopLevel or EventWindow.Gone => true,
        EventWindow.Desktop => kind == Reorder,
        _ => false,
    };

    /// <summary>
    /// A window's own events only, not those of the caret, the pointer or a control inside it, which
    /// are told apart by the event's own fields before any question is put to Windows: the pointer
    /// moves far more often than any window does.
    /// </summary>
    private void OnEvent(nint hook, uint kind, nint window, int item, int child, uint thread, uint time)
    {
        if (item == WindowObject && child == Self && window != 0 && Concerns(kind, Classify(window)))
        {
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static EventWindow Classify(nint window) =>
        !IsWindow(window) ? EventWindow.Gone
        : window == GetDesktopWindow() ? EventWindow.Desktop
        : GetAncestor(window, Root) == window ? EventWindow.TopLevel
        : EventWindow.Inner;

    private delegate void WinEventProc(nint hook, uint kind, nint window, int item, int child, uint thread, uint time);

    // DllImport rather than LibraryImport, matching the shell's other calls: the generator wants
    // AllowUnsafeBlocks across the whole project, and cannot marshal a managed callback.
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}

/// <summary>What the window an event names is, as far as an event that may be read late can tell.</summary>
internal enum EventWindow
{
    /// <summary>A window with no parent but the desktop.</summary>
    TopLevel,

    /// <summary>The desktop itself, which owns the order of the top-level windows.</summary>
    Desktop,

    /// <summary>A window inside another, which covers nothing its top-level window does not.</summary>
    Inner,

    /// <summary>A handle that is no longer a window by the time its event is read.</summary>
    Gone,
}
