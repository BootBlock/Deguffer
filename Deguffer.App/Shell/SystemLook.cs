using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Shell;

/// <summary>
/// The one source for what the system's settings say the app should look like (see
/// <see cref="SystemLookState"/>): read once at each change rather than asked of the system at every use,
/// which a map otherwise did at every pointer move and every frame of a zoom.
///
/// <para>Windows raises its changes off the UI thread. They are read again on the UI thread the first
/// request was made on, and <see cref="Changed"/> is raised there, so <see cref="State"/> is only ever
/// written and read on that thread.</para>
/// </summary>
internal sealed class SystemLook
{
    public static SystemLook Current { get; } = new();

    private readonly UISettings _settings = new();
    private readonly CompositionCapabilities _capabilities = new();
    private readonly DispatcherQueue _queue;

    private SystemLook()
    {
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The system's look is first asked for on the UI thread, where its changes are raised.");

        State = Read();

        _settings.ColorValuesChanged += (_, _) => _queue.TryEnqueueGuarded(Refresh);
        _settings.TextScaleFactorChanged += (_, _) => _queue.TryEnqueueGuarded(Refresh);
        _capabilities.Changed += (_, _) => _queue.TryEnqueueGuarded(Refresh);
    }

    /// <summary>The look as the system last said it was.</summary>
    public SystemLookState State { get; private set; }

    /// <summary>
    /// <see cref="State"/> has moved, in the parts the argument names. Raised on the UI thread, and only
    /// when something did move.
    /// </summary>
    public event EventHandler<SystemLookChange>? Changed;

    /// <summary>
    /// Read the system again now, and raise <see cref="Changed"/> if anything moved. For a caller that
    /// hears of a change before Windows' own event arrives, as a window's theme change does when high
    /// contrast is turned on or off. Call on the UI thread.
    /// </summary>
    public void Refresh()
    {
        var next = Read();
        var change = State.ChangesTo(next);

        if (change == SystemLookChange.None)
        {
            return;
        }

        State = next;
        Changed?.Invoke(this, change);
    }

    private SystemLookState Read() => new(
        HighContrast.IsEnabled()
            ? new ContrastTheme(
                _settings.UIElementColor(UIElementType.Window),
                _settings.UIElementColor(UIElementType.WindowText),
                _settings.UIElementColor(UIElementType.Highlight),
                _settings.UIElementColor(UIElementType.Hotlight))
            : null,
        _settings.GetColorValue(UIColorType.AccentLight2),
        _settings.GetColorValue(UIColorType.AccentDark1),
        _settings.TextScaleFactor,
        _capabilities.AreEffectsFast());
}
