using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// A tool window's one place in the application: it opens the window, or brings forward the one
/// already open, and opens it again at launch where the last session closed with it open.
///
/// <para>Held by the main window, which owns the tool window and outlives it, rather than by the page
/// it serves: the window reopens with the application, before that page has been built.</para>
/// </summary>
internal sealed class ToolWindowOpener
{
    private readonly Func<IToolWindow> _create;
    private readonly WindowPlacementStore<ToolWindowMetrics> _store;
    private readonly IFrameClock _frames;

    private IToolWindow? _window;

    /// <summary>
    /// Whether the application is on its way out, so the window closing with it is not the reader
    /// closing it. Set before the main window goes, which takes this window with it.
    /// </summary>
    private bool _leaving;

    /// <param name="create">Build the window, which is done only when it is first opened.</param>
    /// <param name="store">Where the window was left, and whether it was open.</param>
    /// <param name="frames">The frames the main window draws, which <see cref="Reopen"/> waits on.</param>
    public ToolWindowOpener(Func<IToolWindow> create, WindowPlacementStore<ToolWindowMetrics> store, IFrameClock frames)
    {
        _create = create;
        _store = store;
        _frames = frames;
    }

    /// <summary>Open the window and give it the focus, or bring forward the one already open.</summary>
    public void Show() => Open().Activate();

    /// <summary>
    /// Open the window again where the last session closed with it open, once the main window has
    /// drawn its first frame: built before then, in the launch turn that frame waits for, its whole
    /// tree would put that frame back by the time it takes to build.
    ///
    /// <para>Shown without taking the focus, which stays with the main window the reader started: the
    /// tool window belongs to it and comes forward with it, and a launch that put the keyboard in a
    /// tool window would be a launch into the wrong place.</para>
    /// </summary>
    public void Reopen() => AfterFirstFrame.Run(_frames, () =>
    {
        if (_store.Load() is { IsOpen: true })
        {
            Open().ShowWithoutFocus();
        }
    });

    /// <summary>
    /// Write down whether the window is open and where it is, now, without waiting for a close. For an
    /// elevated replacement, which reads it as it opens, and for the application closing.
    /// </summary>
    public void Remember() => _window?.Remember(isOpen: true);

    /// <summary>
    /// The application is closing, or has been replaced: remember the window as open where it is open,
    /// and take its closing from here on as the application's rather than the reader's.
    /// </summary>
    public void Leave()
    {
        _leaving = true;
        Remember();
    }

    private IToolWindow Open()
    {
        if (_window is null)
        {
            _window = _create();
            _window.Closed += OnClosed;
        }

        return _window;
    }

    /// <summary>
    /// The reader closed the window, so it stays closed at the next launch, or the application closed
    /// it on its way out, which <see cref="Leave"/> has already written down.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_leaving)
        {
            _window?.Remember(isOpen: false);
        }

        _window = null;
    }
}
