namespace Deguffer.Core.Exploring.Layout;

/// <summary>What is moving a map's camera (see <see cref="MapMotion"/>).</summary>
public enum MapMover
{
    /// <summary>Nothing that asks the tracker for more: the camera is where the screen shows, or springing back to it from a drag.</summary>
    Nothing,

    /// <summary>A jump, which rests once nothing has moved the camera again for a moment.</summary>
    Jump,

    /// <summary>The tracker on its own: a glide, a fling, or a hand on the compositor.</summary>
    Tracker,

    /// <summary>A mouse dragging the picture.</summary>
    Mouse,
}
