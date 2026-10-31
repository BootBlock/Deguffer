using Windows.System;

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
/// What each key does on the map, and where each key takes the map's camera, so the picture can be
/// read and moved without a pointer.
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

    /// <summary>The main keyboard's plus (VK_OEM_PLUS), which <see cref="VirtualKey"/> has no name for.</summary>
    private const VirtualKey PlusKey = (VirtualKey)0xBB;

    /// <summary>The main keyboard's minus (VK_OEM_MINUS).</summary>
    private const VirtualKey MinusKey = (VirtualKey)0xBD;

    /// <summary>
    /// What <paramref name="key"/> does, pressed with <paramref name="shift"/>, <paramref name="alt"/>
    /// and <paramref name="ctrl"/> held or not, on the map itself where <paramref name="onMap"/> or on
    /// something inside it, such as the overview's buttons. Null where the map leaves the key alone.
    ///
    /// <para>A key with Alt or Ctrl held is left alone, because Alt+Left and Alt+Right step back and
    /// forward. On the map itself, unshifted, Enter opens the shape the keyboard is on, Backspace goes up
    /// a folder, and an arrow moves between the shapes. Everywhere else, and with Shift, plus and minus
    /// zoom about the middle, the arrows pan, and Home shows the whole picture: on the overview's
    /// buttons the arrows pan as they always have.</para>
    /// </summary>
    public static MapKeyAction? Resolve(VirtualKey key, bool shift, bool alt, bool ctrl, bool onMap)
    {
        if (alt || ctrl)
        {
            return null;
        }

        if (onMap && !shift)
        {
            switch (key)
            {
                case VirtualKey.Enter:
                    return new MapKeyAction.Open();
                case VirtualKey.Back:
                    return new MapKeyAction.Ascend();
                case VirtualKey.Left:
                    return new MapKeyAction.Focus(MapDirection.Left);
                case VirtualKey.Right:
                    return new MapKeyAction.Focus(MapDirection.Right);
                case VirtualKey.Up:
                    return new MapKeyAction.Focus(MapDirection.Up);
                case VirtualKey.Down:
                    return new MapKeyAction.Focus(MapDirection.Down);
            }
        }

        return CameraKey(key) is { } camera ? new MapKeyAction.Camera(camera) : null;
    }

    /// <summary>What <paramref name="key"/> does to the camera, or null where it does nothing.</summary>
    private static MapKey? CameraKey(VirtualKey key) => key switch
    {
        VirtualKey.Add or PlusKey => MapKey.ZoomIn,
        VirtualKey.Subtract or MinusKey => MapKey.ZoomOut,
        VirtualKey.Left => MapKey.Left,
        VirtualKey.Right => MapKey.Right,
        VirtualKey.Up => MapKey.Up,
        VirtualKey.Down => MapKey.Down,
        VirtualKey.Home => MapKey.Whole,
        _ => null,
    };

    /// <summary>Where <paramref name="key"/> takes a camera going to <paramref name="target"/>, no deeper than <paramref name="ceiling"/>.</summary>
    public static MapViewport Step(MapKey key, MapViewport target, double ceiling) => key switch
    {
        MapKey.ZoomIn => target.ZoomedAt(ZoomStep, 0.5, 0.5, ceiling),
        MapKey.ZoomOut => target.ZoomedAt(1 / ZoomStep, 0.5, 0.5, ceiling),

        // Showing what is to the left is the picture dragged to the right.
        MapKey.Left => target.Panned(PanStep, 0),
        MapKey.Right => target.Panned(-PanStep, 0),
        MapKey.Up => target.Panned(0, PanStep),
        MapKey.Down => target.Panned(0, -PanStep),
        MapKey.Whole => MapViewport.Whole,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a key that moves the map."),
    };
}
