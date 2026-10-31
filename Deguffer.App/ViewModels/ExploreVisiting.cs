using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.ViewModels;

/// <summary>What the Explore page has on screen, which a step to or from it is measured against.</summary>
/// <param name="Zooms">Whether a zoom is part of where the reader is. See <see cref="Deguffer.Core.Exploring.Rendering.ExploreSurface.Zooms"/>.</param>
public readonly record struct ExploreOnScreen(ExploreTree Tree, ExplorePosition Position, VolumeSpace Volume, bool Zooms);

/// <summary>
/// Back and Forward on the Explore page, as a browser's go: where the reader has been, zoom and
/// all, and the step to it.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreReplay"/> is, because it
/// holds state of its own, the steps behind and ahead, that the rest of the page never reads. Which
/// step leads where is <see cref="ExploreVisits"/>' to decide. The page owns what is on screen, so it
/// is asked for that (<see cref="ExploreOnScreen"/>) and told to show where a step leads.</para>
/// </summary>
public sealed partial class ExploreVisiting
{
    /// <summary>Where the reader has been, for Back and Forward. See <see cref="ExploreVisits"/>.</summary>
    private readonly ExploreVisits _visits = new();

    /// <summary>What the page has on screen now, or null where it has nothing.</summary>
    private readonly Func<ExploreOnScreen?> _onScreen;

    /// <summary>Show a position of the tree on screen, as a step to somewhere else does.</summary>
    private readonly Action<ExploreTree, ExplorePosition> _show;

    /// <summary>Redraw the place on screen, for a step back to it at another zoom.</summary>
    private readonly Action _redraw;

    /// <param name="onScreen">See <see cref="_onScreen"/>.</param>
    /// <param name="show">See <see cref="_show"/>.</param>
    /// <param name="redraw">See <see cref="_redraw"/>.</param>
    public ExploreVisiting(Func<ExploreOnScreen?> onScreen, Action<ExploreTree, ExplorePosition> show, Action redraw)
    {
        _onScreen = onScreen;
        _show = show;
        _redraw = redraw;
    }

    /// <summary>
    /// The part of the map's picture on screen now, which a step away from here remembers so Back
    /// comes back to it. Asked at the moment of the step rather than followed, because the zoom moves
    /// at every frame of a glide. The page points this at its map, and it is the whole picture until
    /// it does.
    /// </summary>
    public Func<MapViewport> Viewing { get; set; } = () => MapViewport.Whole;

    /// <summary>
    /// The zoom Back or Forward is returning to, for the one redraw that step asks for, and null for
    /// every other. Handed over for that moment only, because every other redraw keeps the zoom the
    /// reader has now: a recolouring, a snapshot, a removal.
    /// </summary>
    public MapViewport? Revisiting { get; private set; }

    /// <summary>
    /// Show <paramref name="to"/>: a step somewhere new, where the reader was going behind them. The
    /// place already on screen is not somewhere new, so it is shown again without being a step, and
    /// what lay ahead stays ahead.
    /// </summary>
    public void Visit(ExplorePosition to)
    {
        if (_onScreen() is not { } on)
        {
            return;
        }

        if (!to.Shows(on.Position, on.Tree, on.Volume))
        {
            _visits.Leave(Here(on));
        }

        _show(on.Tree, to);
    }

    /// <summary>
    /// The map zoomed to a shape it could not open, from <paramref name="from"/>. A step like
    /// opening a folder, so Back zooms out again; a turn of the wheel or a drag is not, as scrolling
    /// a page is not a step in a browser.
    /// </summary>
    public void Zoomed(MapViewport from)
    {
        if (_onScreen() is not { } on)
        {
            return;
        }

        _visits.Leave(new ExploreVisit(on.Position, from));
        Notify();
    }

    /// <summary>
    /// The map's zoom is somewhere new. Where Back and Forward lead turns on it, because a step to the
    /// place on screen at the zoom on screen goes nowhere.
    /// </summary>
    public void ViewportChanged() => Notify();

    /// <summary>
    /// A new tree is replacing <paramref name="leaving"/>, so the steps' node numbers are carried to
    /// <paramref name="arriving"/>. Called before the tree is replaced. See <see cref="ExploreVisits.Carry"/>.
    /// </summary>
    public void Carry(ExploreTree? leaving, ExploreTree arriving) => _visits.Carry(leaving, arriving);

    /// <summary>Say that where Back and Forward lead may have changed.</summary>
    public void Notify()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Back to where the reader was before their last step, as a browser's Back button goes.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_onScreen() is { } on)
        {
            Revisit(on, _visits.Back(Here(on), on.Tree, on.Volume, on.Zooms));
        }
    }

    /// <summary>Forward again to where Back left, as a browser's Forward button goes.</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_onScreen() is { } on)
        {
            Revisit(on, _visits.Forward(Here(on), on.Tree, on.Volume, on.Zooms));
        }
    }

    /// <summary>Where the reader is now, zoom and all, as a step on <see cref="_visits"/>.</summary>
    private ExploreVisit Here(ExploreOnScreen on) => new(on.Position, Viewing());

    /// <summary>
    /// Show <paramref name="visit"/> again, at the zoom it was left at. Within the same place only the
    /// zoom moves, so the rows, the selection and the trail stay as they are.
    /// </summary>
    private void Revisit(ExploreOnScreen on, ExploreVisit? visit)
    {
        if (visit is not { } to)
        {
            return;
        }

        Revisiting = to.Viewport;

        try
        {
            if (to.Position == on.Position)
            {
                _redraw();
            }
            else
            {
                _show(on.Tree, to.Position);
            }
        }
        finally
        {
            Revisiting = null;
        }

        // Asked again once the redraw has put the zoom where it was going: until then the map still
        // answers with the zoom being left, and a step back to the same place at another zoom would
        // read as a step to where the reader already is.
        Notify();
    }

    private bool CanGoBack() => _onScreen() is { } on && _visits.CanGoBack(Here(on), on.Tree, on.Volume, on.Zooms);

    private bool CanGoForward() => _onScreen() is { } on && _visits.CanGoForward(Here(on), on.Tree, on.Volume, on.Zooms);
}
