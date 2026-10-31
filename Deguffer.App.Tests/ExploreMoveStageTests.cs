using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// How the stage the map's moves play on wires the run of one move at a time (<see cref="MapMoveRun"/>)
/// to the compositor: what a move puts on it stays until the move is over, then each visual is let go,
/// and the end of a move is reported once, a move replaced by the next included.
/// </summary>
public sealed class ExploreMoveStageTests
{
    /// <summary>A canvas that lies on the picture as it is.</summary>
    private static readonly MapTransform Unplaced = new(1, 1, 0, 0);

    [Fact]
    public void AMoveFinishedTakesItsVisualsOffAndReportsItsEndOnce()
    {
        var (onStage, moving, released, arrivals, offStage, finished) = CompositionThread.Run(() =>
        {
            var compositor = CompositionThread.Compositor;
            var released = new List<Visual>();
            var stage = new ExploreMoveStage(compositor, new MapCamera(compositor), released.Add);
            var arrivals = 0;

            stage.Arrived += (_, _) => arrivals++;
            stage.Play(Unplaced, Sprites(compositor, 2));

            var onStage = Placed(stage).Count;
            var moving = stage.IsMoving;

            stage.Finish();
            stage.Finish();

            return (onStage, moving, released.Count, arrivals, Placed(stage).Count, !stage.IsMoving);
        });

        Assert.Equal(2, onStage);
        Assert.True(moving);
        Assert.Equal(2, released);
        Assert.Equal(1, arrivals);
        Assert.Equal(0, offStage);
        Assert.True(finished);
    }

    [Fact]
    public void AMovePlayedOverAnotherFinishesItFirst()
    {
        var (released, arrivals, onStage, moving) = CompositionThread.Run(() =>
        {
            var compositor = CompositionThread.Compositor;
            var released = new List<Visual>();
            var stage = new ExploreMoveStage(compositor, new MapCamera(compositor), released.Add);
            var arrivals = 0;

            stage.Arrived += (_, _) => arrivals++;
            stage.Play(Unplaced, Sprites(compositor, 3));
            stage.Play(Unplaced, Sprites(compositor, 1));

            return (released.Count, arrivals, Placed(stage).Count, stage.IsMoving);
        });

        Assert.Equal(3, released);
        Assert.Equal(1, arrivals);
        Assert.Equal(1, onStage);
        Assert.True(moving);
    }

    private static Action<VisualCollection> Sprites(Compositor compositor, int count) => stage =>
    {
        for (var i = 0; i < count; i++)
        {
            stage.InsertAtTop(compositor.CreateSpriteVisual());
        }
    };

    /// <summary>The visuals of the move on <paramref name="stage"/>, under the one that follows the camera.</summary>
    private static VisualCollection Placed(ExploreMoveStage stage) =>
        ((ContainerVisual)((ContainerVisual)stage.Root).Children.Single()).Children;
}
