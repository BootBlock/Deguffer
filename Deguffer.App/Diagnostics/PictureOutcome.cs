namespace Deguffer.App.Diagnostics;

/// <summary>How a picture handed to the map stopped waiting for its first drawing, as <see cref="ShellEvents.PictureStop"/> states it.</summary>
internal enum PictureOutcome
{
    /// <summary>A drawing of it was on screen at the frame the stop was raised at.</summary>
    Shown,

    /// <summary>
    /// Another picture was handed over before any drawing of this one arrived: a scan's snapshots in
    /// quick succession, a map with nothing to draw, or one collapsed for the List view.
    /// </summary>
    Superseded,
}
