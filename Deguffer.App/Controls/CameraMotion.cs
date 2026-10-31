using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Controls;

/// <summary>
/// What is moving a map's camera, <see cref="By"/>, and for a mouse the drag it is making,
/// <see cref="Drag"/>. One value, so a change of what moves the camera ends whatever moved it before:
/// a jump, a glide or a hand during a drag ends the drag, and the next move of the mouse starts a new
/// one from what the screen shows.
/// </summary>
internal readonly record struct CameraMotion
{
    private CameraMotion(CameraMover by, MapStretch? drag)
    {
        By = by;
        Drag = drag;
    }

    /// <summary>Nothing is moving the camera. The default.</summary>
    public static CameraMotion Still => default;

    /// <summary>A jump that has yet to rest.</summary>
    public static CameraMotion Jumped => new(CameraMover.Jump, null);

    /// <summary>The tracker is moving the camera on its own.</summary>
    public static CameraMotion Tracked => new(CameraMover.Tracker, null);

    /// <summary>A mouse is dragging the picture, as <paramref name="drag"/> gives.</summary>
    public static CameraMotion Dragged(MapStretch drag) => new(CameraMover.Mouse, drag);

    public CameraMover By { get; }

    /// <summary>The drag a mouse is making, from its first move past the threshold until it lets go, and null otherwise.</summary>
    public MapStretch? Drag { get; }
}
