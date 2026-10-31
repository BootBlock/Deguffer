namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What is moving a map's camera, <see cref="By"/>, and for a mouse the drag it is making,
/// <see cref="Drag"/>.
///
/// <para>One value, so a change of what moves the camera ends whatever moved it before. A jump, a
/// glide or a hand during a drag ends the drag, and the next move of the mouse starts a new one from
/// what the screen shows: the old drag's place and scale are no longer what the screen shows, and a
/// click is resolved against what it does show (§7.1).</para>
/// </summary>
public readonly record struct MapMotion
{
    private MapMotion(MapMover by, MapStretch? drag)
    {
        By = by;
        Drag = drag;
    }

    /// <summary>Nothing is asking the tracker to move the camera. The default.</summary>
    public static MapMotion Still => default;

    /// <summary>A jump that has yet to rest.</summary>
    public static MapMotion Jumped => new(MapMover.Jump, null);

    /// <summary>The tracker is moving the camera on its own.</summary>
    public static MapMotion Tracked => new(MapMover.Tracker, null);

    /// <summary>A mouse is dragging the picture, as <paramref name="drag"/> gives.</summary>
    public static MapMotion Dragged(MapStretch drag) => new(MapMover.Mouse, drag);

    public MapMover By { get; }

    /// <summary>The drag a mouse is making, from its first move past the threshold until it lets go, and null otherwise.</summary>
    public MapStretch? Drag { get; }

    /// <summary>
    /// Whether the camera waits a moment before it rests: only after a jump, so a run of jumps, such
    /// as a key held down, is drawn once where it stops.
    /// </summary>
    public bool Settles => By == MapMover.Jump;

    /// <summary>
    /// Whether moving the camera by <paramref name="next"/> instead cuts a drag short. The stretch the
    /// drag gave the camera past an edge is the drag's, and the tracker's reports leave it out, so a
    /// drag cut short takes its stretch with it. A drag carrying on does not end, and a hand letting go
    /// is not cut short: its stretch springs back.
    /// </summary>
    public bool CutsShort(MapMotion next) => Drag is not null && !ReferenceEquals(Drag, next.Drag);
}
