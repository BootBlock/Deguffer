using Deguffer.Core.Viewing;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Shell;

/// <summary>
/// The motion Windows' Animation effects setting asks for, followed while the app runs.
///
/// <para>The framework's theme transitions follow the setting on their own. The map's animations are
/// clocked by the app, so nothing follows it for them but this. The answer is kept rather than asked
/// of the system at every frame, and replaced when the setting changes, which Windows raises off the
/// UI thread: hence a volatile read. Windows before 10.0.19041 raises no change, so there the setting
/// is asked afresh each time instead, which a change cannot slip past.</para>
/// </summary>
internal sealed class SystemMotion : IMotionPolicy
{
    public static SystemMotion Current { get; } = new();

    /// <summary>Kept for the life of the app: the change below is only raised while it is alive.</summary>
    private readonly UISettings _settings = new();

    private readonly bool _followed;

    private volatile bool _animationsEnabled;

    private SystemMotion()
    {
        _animationsEnabled = _settings.AnimationsEnabled;

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged += (sender, _) => _animationsEnabled = sender.AnimationsEnabled;
            _followed = true;
        }
    }

    public Motion For(MotionToken token) => token.For(_followed ? _animationsEnabled : _settings.AnimationsEnabled);
}
