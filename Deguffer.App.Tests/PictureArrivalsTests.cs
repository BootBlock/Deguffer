using Deguffer.App.Controls;
using Deguffer.App.Diagnostics;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Tests;

/// <summary>
/// How the map times each new picture for a trace (<see cref="PictureArrivals"/>): from the hand-off to
/// the frame after its first drawing, or to the hand-off of the picture that replaced it, and not at all
/// while no trace listens.
/// </summary>
[Collection(ShellEventsListeningCollection.Name)]
public sealed class PictureArrivalsTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly SteppedFrameClock _clock = new();

    /// <summary>
    /// The wait ends at the frame after the first drawing is presented, not as it is presented, and
    /// stops listening to frames then. A second drawing of the same picture is not a first.
    /// </summary>
    [Fact]
    public void APictureWaitsUntilTheFrameAfterItsFirstDrawing()
    {
        var arrivals = new PictureArrivals(_clock);
        using var recorder = new ShellEventRecorder();

        arrivals.Handed(1, Picture(ExploreView.Sunburst));

        Assert.Equal(["PictureStart"], recorder.Names);
        Assert.Equal([1, (int)ExploreView.Sunburst], recorder.Events[0].Numbers);

        arrivals.Presented();

        Assert.Single(recorder.Events);
        Assert.True(_clock.IsTicking);

        _clock.Step(Frame);
        arrivals.Presented();
        _clock.Step(Frame);

        Assert.Equal(["PictureStart", "PictureStop"], recorder.Names);
        Assert.Equal([1, (int)PictureOutcome.Shown], recorder.Events[1].Numbers);
        Assert.False(_clock.IsTicking);
    }

    /// <summary>A picture replaced before any drawing of it arrived stops as superseded, as the next one starts.</summary>
    [Fact]
    public void APictureReplacedBeforeItsDrawingStopsAsSuperseded()
    {
        var arrivals = new PictureArrivals(_clock);
        using var recorder = new ShellEventRecorder();

        arrivals.Handed(1, Picture(ExploreView.Treemap));
        arrivals.Handed(2, Picture(ExploreView.Treemap));

        Assert.Equal(["PictureStart", "PictureStop", "PictureStart"], recorder.Names);
        Assert.Equal([1, (int)PictureOutcome.Superseded], recorder.Events[1].Numbers);
        Assert.Equal(2, recorder.Events[2].Numbers.First());
    }

    /// <summary>A picture of no tree has nothing to draw, so it is not timed, though it does end the one before.</summary>
    [Fact]
    public void APictureOfNoTreeIsNotTimed()
    {
        var arrivals = new PictureArrivals(_clock);
        using var recorder = new ShellEventRecorder();

        arrivals.Handed(1, Picture(ExploreView.Treemap));
        arrivals.Handed(2, new MapPicture(null, 0, ExploreView.Treemap, VolumeSpace.None));
        arrivals.Presented();

        Assert.Equal(["PictureStart", "PictureStop"], recorder.Names);
        Assert.False(_clock.IsTicking);
    }

    /// <summary>With no trace recording, a picture and its drawing wait on no frame.</summary>
    [Fact]
    public void NothingWaitsOnAFrameWhileNoTraceListens()
    {
        var arrivals = new PictureArrivals(_clock);

        Assert.False(ShellEvents.Log.IsEnabled());

        arrivals.Handed(1, Picture(ExploreView.Treemap));
        arrivals.Presented();

        Assert.False(_clock.IsTicking);
    }

    private static MapPicture Picture(ExploreView view)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.File("pagefile.sys", 100)]);

        return new MapPicture(builder.Build(ExploreChildOrder.BySize), ExploreTreeBuilder.RootNode, view, VolumeSpace.None);
    }
}
