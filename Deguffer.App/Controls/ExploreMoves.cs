using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The map's moves from the drawing on screen to the next as it arrives, rather than the next
/// replacing it a region at a time: the move the redraw on its way owes (<see cref="MapMoves.Owed"/>),
/// the same place laid out again (<see cref="ExploreShift"/>) and the same place in another view
/// (<see cref="ExploreMorph"/>).
///
/// <para>The pointer is over nothing while shapes travel, as it is during a change of folder: a shape
/// is not where it is drawn until it lands, and no shape is where either view puts it (§7.1). So the
/// map hides what does not move with them as one starts (<see cref="Started"/>), and shows the new
/// drawing's again as it lands (<see cref="Landed"/>).</para>
/// </summary>
internal sealed class ExploreMoves
{
    private readonly IMotionPolicy _motion;

    private readonly ExploreShift _shift;

    private readonly ExploreMorph _morph;

    /// <summary>The large shapes moving from one layout of the same place to the next.</summary>
    private readonly ExploreMoveStage _shifting;

    /// <summary>The large shapes travelling from one view to another, over the shapes shifting.</summary>
    private readonly ExploreMoveStage _morphing;

    private readonly ContainerVisual _root;

    /// <summary>The move the redraw on its way makes from the drawing on screen when it arrives.</summary>
    private MapMove _owed;

    public ExploreMoves(Compositor compositor, MapGeometry geometry, MapCamera camera, IMotionPolicy motion)
    {
        _motion = motion;
        _shift = new ExploreShift(compositor);
        _morph = new ExploreMorph(compositor, geometry);
        _shifting = new ExploreMoveStage(compositor, camera, ExploreShift.Release);
        _morphing = new ExploreMoveStage(compositor, camera, ExploreMorph.Release);
        _root = compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(_shifting.Root);
        _root.Children.InsertAtTop(_morphing.Root);
        _shifting.Arrived += OnArrived;
        _morphing.Arrived += OnArrived;
    }

    /// <summary>Raised as shapes start to travel.</summary>
    public event EventHandler? Started;

    /// <summary>Raised once as each move is over, whether it ran its course or was finished early.</summary>
    public event EventHandler? Landed;

    /// <summary>The top of the moving shapes in the composition tree.</summary>
    public Visual Root => _root;

    /// <summary>
    /// Whether the redraw on its way moves from the drawing on screen when it arrives, so what has
    /// landed of it does not answer the pointer yet.
    /// </summary>
    public bool IsOwed => _owed != MapMove.None;

    public bool IsMoving => _shifting.IsMoving || _morphing.IsMoving;

    /// <summary>Whether shapes are travelling from one view to another.</summary>
    public bool IsMorphing => _morphing.IsMoving;

    /// <summary>
    /// Make <paramref name="move"/>, played over <paramref name="motion"/>, as the redraw on its way
    /// arrives, where it owes one: see <see cref="MapMoves.Owed"/>.
    /// </summary>
    public void Owe(MapMove move, Motion motion, bool onItsWay) => _owed = MapMoves.Owed(move, motion.Travels, onItsWay);

    /// <summary>Make no move as the next drawing arrives, for a redraw for any other reason.</summary>
    public void Forgo() => _owed = MapMove.None;

    /// <summary>
    /// Make the move owed from <paramref name="earlier"/>, the drawing that was on screen, to
    /// <paramref name="later"/>, which has just arrived on a canvas that lies at
    /// <paramref name="placed"/> in the picture, out of the drawings <paramref name="pictures"/> keeps.
    /// Returns the move that was owed, which is owed no longer, whether or not anything travels.
    /// </summary>
    public MapMove Arrive(ExploreSurface? earlier, ExploreSurface later, ExploreLayers pictures, MapTransform placed)
    {
        var owed = _owed;

        _owed = MapMove.None;

        if (owed == MapMove.Shift)
        {
            Shift(earlier, later, pictures, placed);
        }
        else if (owed == MapMove.Morph)
        {
            Morph(earlier, later, placed);
        }

        return owed;
    }

    /// <summary>Land every move under way where it was going.</summary>
    public void Finish()
    {
        _shifting.Finish();
        _morphing.Finish();
    }

    /// <summary>
    /// The large shapes travel from where <paramref name="earlier"/> draws them to where
    /// <paramref name="later"/>, the same place laid out again, does (<see cref="ShapeTravel.Between"/>).
    /// </summary>
    private void Shift(ExploreSurface? earlier, ExploreSurface later, ExploreLayers pictures, MapTransform placed)
    {
        var motion = _motion.For(MotionToken.Shift);

        if (!motion.Travels
            || earlier is null
            || pictures.SurfaceOf(earlier) is not { } before
            || pictures.SurfaceOf(later) is not { } after)
        {
            return;
        }

        var travels = ShapeTravel.Between(earlier, later);

        if (travels.Count == 0)
        {
            return;
        }

        _shifting.Play(placed, stage => _shift.Build(stage, travels, before, after, motion));
        Started?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The largest shapes travel from where <paramref name="earlier"/> draws them into
    /// <paramref name="later"/>, the same place in another view (<see cref="ViewMorph"/>).
    /// </summary>
    private void Morph(ExploreSurface? earlier, ExploreSurface later, MapTransform placed)
    {
        var motion = _motion.For(MotionToken.Morph);

        if (!motion.Travels || earlier is null)
        {
            return;
        }

        var morphs = ViewMorph.Between(earlier, later);

        if (morphs.Count == 0)
        {
            return;
        }

        _morphing.Play(placed, stage => _morph.Build(stage, morphs, later.Width, later.Height, motion));
        Started?.Invoke(this, EventArgs.Empty);
    }

    private void OnArrived(object? sender, EventArgs e) => Landed?.Invoke(this, EventArgs.Empty);
}
