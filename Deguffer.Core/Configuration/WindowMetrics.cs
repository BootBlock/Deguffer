using System.Text.Json.Serialization;

namespace Deguffer.Core.Configuration;

/// <summary>
/// A rectangle in physical screen pixels, which is the unit the window itself is placed in.
///
/// <para>Stored unscaled and restored unscaled. A remembered placement is a point on a desktop
/// rather than a measurement of content, so converting it through the DPI of whichever display the
/// window happens to open on would move it: the same numbers would land somewhere else on a machine
/// whose displays are scaled differently from the one that wrote them.</para>
/// </summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// These bounds moved and sized to sit wholly inside <paramref name="work"/>, no smaller than
    /// the given floor.
    ///
    /// <para>The display a window was left on can be smaller, scaled differently, or gone by the
    /// next launch, and Windows will place a window where no display reaches. Such a window cannot
    /// be dragged back, because the title bar it would be dragged by is the part that is off
    /// screen, so the position is pulled in rather than trusted.</para>
    ///
    /// <para>The work area wins over the floor where the two disagree. A window taller than the
    /// desktop cannot be resized back by dragging either, so a display too small for the minimum
    /// layout gets a cramped window rather than an unreachable one.</para>
    ///
    /// <para><paramref name="work"/> is a display's work area, so both its extents are positive
    /// and the clamped range below can never run backwards.</para>
    /// </summary>
    public WindowBounds Within(WindowBounds work, int minimumWidth, int minimumHeight)
    {
        var width = Math.Min(Math.Max(Width, minimumWidth), work.Width);
        var height = Math.Min(Math.Max(Height, minimumHeight), work.Height);

        return new WindowBounds(
            Math.Clamp(X, work.X, work.X + work.Width - width),
            Math.Clamp(Y, work.Y, work.Y + work.Height - height),
            width,
            height);
    }

    /// <summary>
    /// A window <paramref name="width"/> by <paramref name="height"/> beside these bounds, which are the
    /// window it belongs to, where it covers as little of them as <paramref name="work"/> allows, with
    /// <paramref name="gap"/> pixels between the two.
    ///
    /// <para>To the right where the work area has room, then to the left, level with the owner's top.
    /// Only where neither side has room (the owner maximised, most often) over the owner's right-hand
    /// edge, three gaps in and <paramref name="drop"/> pixels down from its top, below its title bar and
    /// toolbar. That last place does cover some of the owner, and the reader can move the window from
    /// there. Always wholly inside <paramref name="work"/>, as <see cref="Within"/> holds.</para>
    /// </summary>
    public WindowBounds Beside(WindowBounds work, int width, int height, int gap, int drop)
    {
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);

        var right = X + Width + gap;
        var left = X - gap - width;

        var (x, y) = right + width <= work.X + work.Width ? (right, Y)
            : left >= work.X ? (left, Y)
            : (X + Width - width - (gap * 3), Y + drop);

        return new WindowBounds(
            Math.Clamp(x, work.X, work.X + work.Width - width),
            Math.Clamp(y, work.Y, work.Y + work.Height - height),
            width,
            height);
    }
}

/// <summary>
/// Where the main window was left, so the next launch opens where the last one closed.
///
/// <para>A file under <c>%LOCALAPPDATA%\Deguffer</c> rather than a field on
/// <see cref="AppPreferences"/>, for the reason <see cref="SourceRootStore"/> and
/// <see cref="SelectionStore"/> both give: that record is a set of choices the user made on the
/// Settings page, and this is not a choice at all. Nobody decides to put a window at 412, 96. It is
/// a side effect of using the application, written on every close, and folding it in would have the
/// Settings file rewritten by a window drag.</para>
/// </summary>
/// <param name="Bounds">
/// The <em>restored</em> rectangle, even where the window closed maximised. A maximised window has
/// to have somewhere to go when it is restored, and the maximised rectangle is not it: it overhangs
/// the display by the invisible resize border, so restoring it as an ordinary window gives one that
/// looks maximised, is not, and cannot be un-maximised.
/// </param>
/// <param name="IsMaximized">
/// Whether the window was maximised. Minimised is deliberately not a third state — a window
/// remembered as minimised would open with nothing on screen, and the user asked for the
/// application.
/// </param>
public sealed record WindowMetrics(WindowBounds Bounds, bool IsMaximized) : IWindowPlacement
{
    /// <summary>
    /// Whether this describes a window at all. A zero or negative extent is not a small window, so
    /// it is treated as nothing remembered rather than clamped up into a placement the user never
    /// left the window at.
    ///
    /// Kept out of the file: a derived value written beside the two it is derived from reads, to
    /// anyone editing the file by hand, as a third thing they can set.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => Bounds.Width > 0 && Bounds.Height > 0;
}
