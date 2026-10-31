using Deguffer.Core.Exploring.Layout;
using Windows.System;

namespace Deguffer.Core.Tests;

/// <summary>What each key does on the map, and where each key takes the map's camera, so the picture can be read and moved without a pointer.</summary>
public sealed class MapKeysTests
{
    private const double Precision = 1e-9;

    private static readonly MapViewport Zoomed = MapViewport.Anchored(8, 0.5, 0.5, 0.5, 0.5, MapCeiling.Least);

    /// <summary>Plus and minus zoom about the middle of the screen: what is there stays there.</summary>
    [Theory]
    [InlineData(MapKey.ZoomIn, 16)]
    [InlineData(MapKey.ZoomOut, 4)]
    public void PlusAndMinusZoomAboutTheMiddle(MapKey key, double zoom)
    {
        var from = MapViewport.Anchored(8, 0.3, 0.6, 0.5, 0.5, MapCeiling.Least);

        var to = MapKeys.Step(key, from, MapCeiling.Least);
        var (x, y) = to.PictureAt(0.5, 0.5);

        Assert.Equal(zoom, to.Zoom, Precision);
        Assert.Equal(0.3, x, Precision);
        Assert.Equal(0.6, y, Precision);
    }

    /// <summary>An arrow shows what lies that way, by a fifth of the screen.</summary>
    [Theory]
    [InlineData(MapKey.Left, -1, 0)]
    [InlineData(MapKey.Right, 1, 0)]
    [InlineData(MapKey.Up, 0, -1)]
    [InlineData(MapKey.Down, 0, 1)]
    public void AnArrowShowsWhatLiesThatWay(MapKey key, int across, int down)
    {
        var to = MapKeys.Step(key, Zoomed, MapCeiling.Least);

        Assert.Equal(Zoomed.Zoom, to.Zoom);
        Assert.Equal(Zoomed.Left + (across * MapKeys.PanStep / Zoomed.Zoom), to.Left, Precision);
        Assert.Equal(Zoomed.Top + (down * MapKeys.PanStep / Zoomed.Zoom), to.Top, Precision);
    }

    [Fact]
    public void HomeShowsTheWholePicture() => Assert.Equal(MapViewport.Whole, MapKeys.Step(MapKey.Whole, Zoomed, MapCeiling.Least));

    /// <summary>The whole picture has nowhere further out to go, and nothing beside it to pan to.</summary>
    [Theory]
    [InlineData(MapKey.ZoomOut)]
    [InlineData(MapKey.Left)]
    [InlineData(MapKey.Down)]
    public void TheWholePictureStaysWhole(MapKey key) => Assert.Equal(MapViewport.Whole, MapKeys.Step(key, MapViewport.Whole, MapCeiling.Least));

    /// <summary>Alt+Left and Alt+Right step back and forward, so the map leaves every key alone with Alt or Ctrl held.</summary>
    [Theory]
    [InlineData(VirtualKey.Left, true, false)]
    [InlineData(VirtualKey.Right, false, true)]
    [InlineData(VirtualKey.Enter, true, false)]
    [InlineData(VirtualKey.Back, false, true)]
    [InlineData(VirtualKey.Add, true, false)]
    [InlineData(VirtualKey.Home, false, true)]
    public void AltOrCtrlLeavesEveryKeyAlone(VirtualKey key, bool alt, bool ctrl)
    {
        Assert.Null(MapKeys.Resolve(key, shift: false, alt, ctrl, onMap: true));
        Assert.Null(MapKeys.Resolve(key, shift: false, alt, ctrl, onMap: false));
    }

    [Theory]
    [InlineData(VirtualKey.Left, MapDirection.Left)]
    [InlineData(VirtualKey.Right, MapDirection.Right)]
    [InlineData(VirtualKey.Up, MapDirection.Up)]
    [InlineData(VirtualKey.Down, MapDirection.Down)]
    public void AnUnshiftedArrowOnTheMapMovesBetweenTheShapes(VirtualKey key, MapDirection direction) =>
        Assert.Equal(new MapKeyAction.Focus(direction), MapKeys.Resolve(key, shift: false, alt: false, ctrl: false, onMap: true));

    /// <summary>With Shift, or on the overview's buttons inside the map, an arrow pans the picture.</summary>
    [Theory]
    [InlineData(VirtualKey.Left, MapKey.Left, true, true)]
    [InlineData(VirtualKey.Right, MapKey.Right, true, true)]
    [InlineData(VirtualKey.Up, MapKey.Up, true, false)]
    [InlineData(VirtualKey.Down, MapKey.Down, false, false)]
    [InlineData(VirtualKey.Left, MapKey.Left, false, false)]
    public void AnArrowWithShiftOrOffTheMapPans(VirtualKey key, MapKey camera, bool shift, bool onMap) =>
        Assert.Equal(new MapKeyAction.Camera(camera), MapKeys.Resolve(key, shift, alt: false, ctrl: false, onMap));

    /// <summary>Plus, minus and Home move the camera wherever the key lands, with Shift or without.</summary>
    [Theory]
    [InlineData(VirtualKey.Add, MapKey.ZoomIn)]
    [InlineData((VirtualKey)0xBB, MapKey.ZoomIn)]
    [InlineData(VirtualKey.Subtract, MapKey.ZoomOut)]
    [InlineData((VirtualKey)0xBD, MapKey.ZoomOut)]
    [InlineData(VirtualKey.Home, MapKey.Whole)]
    public void PlusMinusAndHomeMoveTheCamera(VirtualKey key, MapKey camera)
    {
        foreach (var shift in new[] { false, true })
        {
            foreach (var onMap in new[] { false, true })
            {
                Assert.Equal(new MapKeyAction.Camera(camera), MapKeys.Resolve(key, shift, alt: false, ctrl: false, onMap));
            }
        }
    }

    [Fact]
    public void EnterOnTheMapOpens() =>
        Assert.Equal(new MapKeyAction.Open(), MapKeys.Resolve(VirtualKey.Enter, shift: false, alt: false, ctrl: false, onMap: true));

    [Fact]
    public void BackspaceOnTheMapGoesUp() =>
        Assert.Equal(new MapKeyAction.Ascend(), MapKeys.Resolve(VirtualKey.Back, shift: false, alt: false, ctrl: false, onMap: true));

    /// <summary>
    /// Enter and Backspace with Shift, or on the overview's buttons, are left for whatever they reach:
    /// the button presses, and the page opens what is picked or goes back.
    /// </summary>
    [Theory]
    [InlineData(VirtualKey.Enter, true, true)]
    [InlineData(VirtualKey.Enter, false, false)]
    [InlineData(VirtualKey.Back, true, true)]
    [InlineData(VirtualKey.Back, false, false)]
    public void EnterAndBackspaceActOnlyUnshiftedOnTheMap(VirtualKey key, bool shift, bool onMap) =>
        Assert.Null(MapKeys.Resolve(key, shift, alt: false, ctrl: false, onMap));

    [Theory]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.Escape)]
    [InlineData(VirtualKey.End)]
    [InlineData(VirtualKey.Space)]
    public void AnyOtherKeyIsLeftAlone(VirtualKey key)
    {
        Assert.Null(MapKeys.Resolve(key, shift: false, alt: false, ctrl: false, onMap: true));
        Assert.Null(MapKeys.Resolve(key, shift: true, alt: false, ctrl: false, onMap: false));
    }
}
