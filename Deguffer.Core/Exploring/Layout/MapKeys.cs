namespace Deguffer.Core.Exploring.Layout;

/// <summary>A key that moves the map's camera.</summary>
public enum MapKey
{
    /// <summary>Plus: magnify about the middle of the screen.</summary>
    ZoomIn,

    /// <summary>Minus: the reverse of <see cref="ZoomIn"/>.</summary>
    ZoomOut,

    /// <summary>The left arrow: show what is to the left.</summary>
    Left,

    /// <summary>The right arrow.</summary>
    Right,

    /// <summary>The up arrow.</summary>
    Up,

    /// <summary>The down arrow.</summary>
    Down,

    /// <summary>Home: the whole picture.</summary>
    Whole,
}

/// <summary>
/// Where each key takes the map's camera, so the picture can be moved without a pointer.
///
/// <para>Each step is taken from where the camera is going rather than from what is on screen, so a
/// key held down, or pressed several times quickly, adds up to as many steps, as a run of wheel
/// notches does.</para>
/// </summary>
public static class MapKeys
{
    /// <summary>How much one press of plus or minus magnifies: twice, as a map application's zoom level does.</summary>
    public const double ZoomStep = 2;

    /// <summary>How far one press of an arrow moves the picture, as a fraction of the screen.</summary>
    public const double PanStep = 0.2;

    /// <summary>Where <paramref name="key"/> takes a camera going to <paramref name="target"/>.</summary>
    public static MapViewport Step(MapKey key, MapViewport target) => key switch
    {
        MapKey.ZoomIn => target.ZoomedAt(ZoomStep, 0.5, 0.5),
        MapKey.ZoomOut => target.ZoomedAt(1 / ZoomStep, 0.5, 0.5),

        // Showing what is to the left is the picture dragged to the right.
        MapKey.Left => target.Panned(PanStep, 0),
        MapKey.Right => target.Panned(-PanStep, 0),
        MapKey.Up => target.Panned(0, PanStep),
        MapKey.Down => target.Panned(0, -PanStep),
        MapKey.Whole => MapViewport.Whole,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a key that moves the map."),
    };
}
