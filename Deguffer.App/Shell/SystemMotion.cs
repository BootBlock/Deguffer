using Deguffer.Core.Viewing;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Shell;

/// <summary>
/// The motion Windows' Animation effects setting asks for, followed while the app runs.
///
/// <para>The framework's theme transitions follow the setting on their own. The map's animations are
/// clocked by the app, so nothing follows it for them but this.</para>
///
/// <para>Asked of the system at every request rather than kept and updated on
/// <c>AnimationsEnabledChanged</c>. In an unpackaged desktop process that event was not raised when
/// the setting was turned off, while the property read the new value at once, so a kept answer would
/// go on animating for a reader who had just turned animation off. The read is cheap enough for every
/// frame of a quarter-second move.</para>
/// </summary>
internal sealed class SystemMotion : IMotionPolicy
{
    public static SystemMotion Current { get; } = new();

    private readonly UISettings _settings = new();

    private SystemMotion()
    {
    }

    public Motion For(MotionToken token) => token.For(_settings.AnimationsEnabled);
}
