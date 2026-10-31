using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One surface a map keeps a drawing of its picture on, as <see cref="KeptDrawings"/> sees it: what
/// is on it, of which part of which picture, and when it was last shown.
/// </summary>
public interface IKeptDrawing
{
    /// <summary>The part of the picture the drawing shows, or will once it has landed.</summary>
    MapViewport Viewport { get; }

    /// <summary>Which picture the drawing is of, on a count that goes up each time the picture changes.</summary>
    int Picture { get; }

    /// <summary>When it was last shown, on a count that goes up with each drawing shown.</summary>
    long LastShown { get; }

    /// <summary>Whether the whole drawing is on the surface.</summary>
    bool HasDrawing { get; }

    /// <summary>Whether a redraw is landing on the surface.</summary>
    bool IsLanding { get; }

    /// <summary>
    /// Whether the redraw landing here replaces the drawing on top, and so shows over it while it
    /// lands, rather than going under it.
    /// </summary>
    bool LandsOnTop { get; }

    /// <summary>The width of the canvas the surface holds, in pixels.</summary>
    int CanvasWidth { get; }

    /// <summary>The height of the canvas the surface holds, in pixels.</summary>
    int CanvasHeight { get; }

    /// <summary>Whether anything of a drawing is on the surface to be seen.</summary>
    bool IsShown => HasDrawing || IsLanding;
}
