using System.Text.Json.Serialization;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Where a tool window was left, and whether it was open when the application closed, so the next
/// launch puts it back where the reader had it, open or not.
///
/// <para>A file of its own rather than a field on <see cref="AppPreferences"/>, for the reason
/// <see cref="WindowMetrics"/> gives: nobody chooses to leave a window at 412, 96, and folding it in
/// would have the settings file rewritten by a drag.</para>
/// </summary>
/// <param name="Bounds">
/// The window's rectangle in physical screen pixels, unscaled as <see cref="WindowBounds"/> says. A
/// tool window is never maximised or minimised on its own, so this is always the rectangle it was
/// drawn in.
/// </param>
/// <param name="IsOpen">
/// Whether the window was open when the application closed. Closing the window says no; closing the
/// application with the window open says it is to open again with it.
/// </param>
public sealed record ToolWindowMetrics(WindowBounds Bounds, bool IsOpen) : IWindowPlacement
{
    /// <summary>
    /// Whether this describes a window at all: a zero or negative extent is nothing remembered, as
    /// <see cref="WindowMetrics.IsUsable"/> holds. Kept out of the file for the reason given there.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => Bounds.Width > 0 && Bounds.Height > 0;
}
