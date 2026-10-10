using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>Where a turn of the wheel takes the map's camera.</summary>
public sealed class MapWheelTests
{
    private const double Precision = 1e-9;

    private const int Notch = (int)MapWheel.Notch;

    [Fact]
    public void ThreeNotchesDoubleTheZoomAndKeepWhatIsUnderThePointerThere()
    {
        var shown = MapViewport.Anchored(2, 0.4, 0.6, 0.5, 0.5);
        var under = shown.PictureAt(0.3, 0.7);

        var to = MapWheel.Zoom(shown, shown, 3 * Notch, 0.3, 0.7);

        Assert.Equal(4, to.Zoom, Precision);
        Assert.Equal(under.X, to.PictureAt(0.3, 0.7).X, Precision);
        Assert.Equal(under.Y, to.PictureAt(0.3, 0.7).Y, Precision);
    }

    /// <summary>
    /// A run of notches faster than the glide adds up: each is taken from where the camera is going,
    /// and taken from the screen instead, a quick turn would zoom by one notch however far it went.
    /// </summary>
    [Fact]
    public void ANotchIsTakenFromWhereTheCameraIsGoing()
    {
        var shown = MapViewport.Anchored(2, 0.5, 0.5, 0.5, 0.5);
        var going = MapViewport.Anchored(4, 0.5, 0.5, 0.5, 0.5);

        Assert.Equal(8, MapWheel.Zoom(going, shown, 3 * Notch, 0.5, 0.5).Zoom, Precision);
    }

    [Fact]
    public void TurningTowardTheReaderZoomsOut() =>
        Assert.Equal(1, MapWheel.Zoom(MapViewport.Anchored(2, 0.5, 0.5, 0.5, 0.5), MapViewport.Whole, -3 * Notch, 0.5, 0.5).Zoom, Precision);

    /// <summary>Across, a turn away from the reader shows what is to the left, as a scroll bar does, by a fifth of the screen a notch.</summary>
    [Fact]
    public void ATurnAcrossAwayFromTheReaderShowsWhatIsToTheLeft()
    {
        var from = MapViewport.Anchored(8, 0.5, 0.5, 0.5, 0.5);

        var to = MapWheel.Across(from, 2 * Notch);

        Assert.Equal(from.Zoom, to.Zoom);
        Assert.Equal(from.Top, to.Top);
        Assert.Equal(from.Left - (2 * MapKeys.PanStep / from.Zoom), to.Left, Precision);
    }
}
