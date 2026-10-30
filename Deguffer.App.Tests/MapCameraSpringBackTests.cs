using System.Numerics;
using Deguffer.App.Controls;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// A drag held at the picture's edge can round a hair past it, so a reader with animation effects off
/// lets go into a spring back with an instant motion. The compositor refuses a key frame animation
/// shorter than a millisecond, and the refusal escaped the pointer handler and ended the process.
/// </summary>
public sealed class MapCameraSpringBackTests
{
    [Fact]
    public void WithMotionOffASpringBackTakesTheStretchAwayAndLandsAtOnce()
    {
        var (landings, stretch) = CompositionThread.Run(() =>
        {
            var camera = new MapCamera(CompositionThread.Compositor);
            var landings = 0;

            camera.Stretch(30, -12);
            camera.SpringBack(MotionToken.Camera.Reduced, () => landings++);
            camera.Properties.TryGetVector3("Stretch", out var stretch);

            return (landings, stretch);
        });

        Assert.Equal(1, landings);
        Assert.Equal(Vector3.Zero, stretch);
    }
}
