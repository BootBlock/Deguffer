using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Deguffer.App.Shell;

/// <summary>
/// Makes a small window belong to the main one, and opens it where it covers as little of it as the
/// screen allows.
///
/// <para>Belonging is Win32 ownership: the small window stays above the main one, goes when it is
/// minimised and comes back with it, and has no taskbar button of its own. WinUI has no property for
/// it, so it is set on the window handle, as <see cref="WindowSizing"/> sets the minimum size.</para>
/// </summary>
public static class ToolWindowPlacement
{
    /// <summary>The gap left between the two windows, in device-independent pixels.</summary>
    private const int Gap = 8;

    private const int GwlpHwndParent = -8;

    /// <summary>
    /// Give <paramref name="tool"/> to <paramref name="owner"/>, size it to <paramref name="width"/>
    /// by <paramref name="height"/> device-independent pixels, and put it beside the owner.
    ///
    /// <para>To the right where the work area has room, then to the left, and only where neither
    /// side has room — the owner maximised, most often — over the owner's right-hand edge, level
    /// with the top of its content. The map fills the owner, so that last place does cover some of
    /// it, and the reader can move the window from there.</para>
    /// </summary>
    public static void Place(Window tool, Window owner, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(owner);

        var toolHandle = WinRT.Interop.WindowNative.GetWindowHandle(tool);
        var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);

        _ = WindowLong.Set(toolHandle, GwlpHwndParent, ownerHandle);

        var scale = owner.Content?.XamlRoot?.RasterizationScale ?? 1;
        var size = new SizeInt32((int)(width * scale), (int)(height * scale));
        var gap = (int)(Gap * scale);

        var ownerBounds = new RectInt32(
            owner.AppWindow.Position.X, owner.AppWindow.Position.Y, owner.AppWindow.Size.Width, owner.AppWindow.Size.Height);
        var work = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        var top = Math.Clamp(ownerBounds.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
        var right = ownerBounds.X + ownerBounds.Width + gap;
        var left = ownerBounds.X - gap - size.Width;

        var position = right + size.Width <= work.X + work.Width
            ? new PointInt32(right, top)
            : left >= work.X
                ? new PointInt32(left, top)
                : new PointInt32(
                    Math.Max(work.X, ownerBounds.X + ownerBounds.Width - size.Width - (gap * 3)),
                    Math.Clamp(ownerBounds.Y + (int)(96 * scale), work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height)));

        tool.AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, size.Width, size.Height));
    }
}
