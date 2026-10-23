using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// The map appearance window's one place in the application: it opens the window, or brings forward
/// the one already open, and opens it again at launch where the last session closed with it open.
///
/// <para>Held by the main window, which owns the appearance window and outlives it, rather than by the
/// Explore page: the window reopens with the application, before the page has been built.</para>
/// </summary>
public sealed class MapAppearanceOpener
{
    private readonly Window _owner;
    private readonly MapAppearanceViewModel _appearance;
    private readonly WindowPlacementStore<ToolWindowMetrics> _store;

    private MapAppearanceWindow? _window;

    /// <summary>
    /// Whether the application is on its way out, so the window closing with it is not the reader
    /// closing it. Set before the main window goes, which takes this window with it.
    /// </summary>
    private bool _leaving;

    public MapAppearanceOpener(Window owner, MapAppearanceViewModel appearance, WindowPlacementStore<ToolWindowMetrics> store)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(store);

        _owner = owner;
        _appearance = appearance;
        _store = store;
    }

    /// <summary>Open the window and give it the focus, or bring forward the one already open.</summary>
    public void Show() => Open().Activate();

    /// <summary>
    /// Open the window again where the last session closed with it open. Shown without taking the
    /// focus, which stays with the main window the reader started: the appearance window belongs to it
    /// and comes forward with it, and a launch that put the keyboard in a tool window would be a launch
    /// into the wrong place.
    /// </summary>
    public void Reopen()
    {
        if (_store.Load() is { IsOpen: true })
        {
            Open().ShowWithoutFocus();
        }
    }

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

    private MapAppearanceWindow Open()
    {
        if (_window is null)
        {
            _window = new MapAppearanceWindow(_appearance, _owner, _store);
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
