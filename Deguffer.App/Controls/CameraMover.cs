namespace Deguffer.App.Controls;

/// <summary>What is moving a map's camera (see <see cref="CameraMotion"/>).</summary>
internal enum CameraMover
{
    /// <summary>Nothing: the camera is at rest where the screen shows.</summary>
    Nothing,

    /// <summary>A jump, which rests once nothing has moved the camera again for a moment.</summary>
    Jump,

    /// <summary>The tracker on its own: a glide, a fling, or a hand on the compositor.</summary>
    Tracker,

    /// <summary>A mouse dragging the picture.</summary>
    Mouse,
}
