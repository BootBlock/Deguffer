using Deguffer.Core.Configuration;

namespace Deguffer.Core.Viewing;

/// <summary>
/// Whether the navigation rail shows its labels or folds to its icons, from the window's width and
/// the reader's <see cref="RailChoice"/>.
///
/// <para>The rail stays in the window's Left mode at every width, so a folded rail is the icon strip
/// beside the page and an open one pushes the page over: it never overlays the page. The control's own
/// Auto mode was not used, because its compact mode opens the pane as an overlay that a click
/// elsewhere dismisses, and a reader who asked for the labels on a narrow window would lose them at
/// the next click.</para>
/// </summary>
public static class RailFold
{
    /// <summary>
    /// The narrowest window, in effective pixels, that opens the rail on its own. Every page is laid
    /// out to fit beside the open rail at this width, and the first launch's 1000 pixel window is
    /// above it, so a new reader sees every destination named.
    /// </summary>
    public const double OpenWidth = 880;

    /// <summary>Whether the rail shows its labels in a window <paramref name="windowWidth"/> effective pixels wide.</summary>
    public static bool IsOpen(RailChoice choice, double windowWidth) => choice switch
    {
        RailChoice.Open => true,
        RailChoice.Collapsed => false,
        _ => OpensOnItsOwn(windowWidth),
    };

    /// <summary>
    /// What the reader chose by opening or folding the rail with its toggle in a window
    /// <paramref name="windowWidth"/> effective pixels wide.
    ///
    /// <para>A choice is stored only where it differs from what the width would do. Folding a rail
    /// that would open makes it stay folded on every window, and folding one back where the width
    /// folds it anyway returns it to <see cref="RailChoice.Automatic"/>. So the toggle is always the
    /// way back to the automatic fold, and nothing else is needed to undo a choice.</para>
    /// </summary>
    public static RailChoice Chosen(bool open, double windowWidth) =>
        open == OpensOnItsOwn(windowWidth) ? RailChoice.Automatic
        : open ? RailChoice.Open
        : RailChoice.Collapsed;

    private static bool OpensOnItsOwn(double windowWidth) => windowWidth >= OpenWidth;
}
