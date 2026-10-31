using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// Where one kind of the map's moves plays: visuals that follow the map's camera, as every drawing
/// does, placed where the canvas the move is drawn on lies in the picture, one move at a time
/// (<see cref="MapMoveRun"/>). What the visuals are and how they travel is the move's own
/// (<see cref="ExploreShift"/>, <see cref="ExploreMorph"/>).
///
/// <para>Decoration only. The visuals go when the move lands, leaving the drawing that has faded in
/// under them over the same time. The crossfade is the layers' to bring to its end
/// (<see cref="ExploreLayers.LandFades"/>).</para>
/// </summary>
internal sealed class ExploreMoveStage
{
    private readonly Compositor _compositor;

    /// <summary>Follows the map's camera, as every drawing does.</summary>
    private readonly ContainerVisual _camera;

    /// <summary>Where the canvas the move is drawn on lies in the picture, which the camera places on the screen.</summary>
    private readonly ContainerVisual _placed;

    /// <summary>Lets go of one of the move's visuals once it is off the stage.</summary>
    private readonly Action<Visual> _release;

    private readonly MapMoveRun _run = new();

    /// <param name="release">Lets go of one of the move's visuals, and what it alone uses, once it is off the stage.</param>
    public ExploreMoveStage(Compositor compositor, MapCamera camera, Action<Visual> release)
    {
        _compositor = compositor;
        _release = release;
        _camera = compositor.CreateContainerVisual();
        _placed = compositor.CreateContainerVisual();
        _camera.Children.InsertAtTop(_placed);
        camera.Follow(_camera);
    }

    /// <summary>Raised once when a move is over, whether it ran its course or was finished early.</summary>
    public event EventHandler? Arrived;

    /// <summary>The top of the moving visuals in the composition tree.</summary>
    public Visual Root => _camera;

    public bool IsMoving => _run.IsMoving;

    /// <summary>
    /// Finish the move under way, and play another on a canvas that lies at <paramref name="placed"/>
    /// in the picture: <paramref name="build"/> puts its visuals on the stage and starts their
    /// animations, and the move is over when every one of them is.
    /// </summary>
    public void Play(MapTransform placed, Action<VisualCollection> build)
    {
        Finish();

        _placed.Place(placed);

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        build(_placed.Children);

        batch.End();

        var run = _run.Start();

        batch.Completed += (_, _) =>
        {
            if (_run.Completes(run))
            {
                Finish();
            }
        };
    }

    /// <summary>End the move where it was going: its visuals go.</summary>
    public void Finish()
    {
        if (!_run.Finish())
        {
            return;
        }

        foreach (var visual in _placed.Children.ToList())
        {
            _placed.Children.Remove(visual);
            _release(visual);
        }

        Arrived?.Invoke(this, EventArgs.Empty);
    }
}
