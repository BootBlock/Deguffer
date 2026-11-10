namespace Deguffer.Core.Viewing;

/// <summary>
/// Whether a part of one of Deguffer's windows is hidden from the reader: behind other windows, off
/// every screen, or in a window the compositor holds off the screen, as it holds one on another
/// virtual desktop.
///
/// <para>Windows reports a window minimised or hidden to the XAML host, and nothing at all for one
/// that other windows cover, so this works it out from the windows in front.</para>
///
/// <para><b>Every doubt answers "seen".</b> A field that goes on moving behind a window costs only
/// processor time, where one stopped in front of the reader is a fault they can see. So a window
/// whose facts cannot be read covers nothing, and a desktop that cannot be read hides nothing.</para>
/// </summary>
public static class WindowCover
{
    /// <summary>Whether nothing of <paramref name="target"/>, a part of <paramref name="window"/>, can be seen.</summary>
    /// <param name="window">The top-level window the part is in.</param>
    /// <param name="target">Where the part is on the desktop.</param>
    public static bool Hides(nint window, ScreenRect target) => Hides(WindowStack.Instance, window, target);

    internal static bool Hides(IWindowStack stack, nint window, ScreenRect target)
    {
        if (stack.IsCloaked(window) is true)
        {
            return true;
        }

        if (stack.FrontToBack() is not { } windows || stack.Screens() is not { Count: > 0 } screens)
        {
            return false;
        }

        var index = IndexOf(windows, window);

        return index >= 0 && ScreenCover.IsHidden(target, screens, Covers(stack, windows, index));
    }

    private static int IndexOf(IReadOnlyList<nint> windows, nint window)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i] == window)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The frames of the windows in front that hide what is behind them: shown, not minimised, not
    /// cloaked, and solid. Lazy, so a target found hidden by the first few reads no more windows.
    /// </summary>
    private static IEnumerable<ScreenRect> Covers(IWindowStack stack, IReadOnlyList<nint> windows, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var window = windows[i];

            if (stack.IsVisible(window)
                && !stack.IsMinimised(window)
                && stack.IsCloaked(window) is false
                && stack.IsSolid(window)
                && stack.Frame(window) is { } frame)
            {
                yield return frame;
            }
        }
    }
}
