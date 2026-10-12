using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Deguffer.App.Controls;

/// <summary>
/// The map as a screen reader reads it: a tree of the shapes it draws, each folder's shapes under the
/// folder's own (<see cref="MapShapeAutomationPeer"/>), and the shape the keyboard is on as the focus.
///
/// <para>The shapes are not elements, so the peers for them are made here and kept for as long as the
/// drawing they describe is the one on screen: a screen reader asks for the same shape many times, and
/// expects the same peer back each time. A new drawing is a new set, and the tree says its children
/// changed.</para>
///
/// <para>A folder's shapes are in the tree only along the keyboard's path and where the reader expanded
/// the folder (<see cref="MapFolders.Lists"/>), so the tree is the size of what the reader reached
/// rather than of the picture.</para>
/// </summary>
internal sealed partial class MapAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
{
    private readonly ExploreMap _map;

    /// <summary>The peers made for the drawing in <see cref="_drawn"/>, by folder and shape.</summary>
    private readonly Dictionary<(int? Folder, int Node), MapShapeAutomationPeer> _peers = [];

    /// <summary>
    /// Each folder's peers in order, for the drawing in <see cref="_drawn"/>, once asked. A search of
    /// the window asks for a folder's children at each step through them.
    /// </summary>
    private readonly Dictionary<int, IReadOnlyList<MapShapeAutomationPeer>> _held = [];

    /// <summary>The peers at the top of the picture, as <see cref="_held"/> keeps a folder's.</summary>
    private IReadOnlyList<MapShapeAutomationPeer>? _top;

    private object? _drawn;

    /// <summary>The keyboard's folder the tree was last given for, or null for the top of the picture.</summary>
    private int? _listedFor;

    public MapAutomationPeer(ExploreMap map)
        : base(map)
    {
        _map = map;
    }

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    /// <summary>
    /// The peers of <paramref name="folder"/>'s shapes, or the top of the picture's for null, as the
    /// drawing on screen draws them: none of a folder that is collapsed (<see cref="MapFolders.Listed"/>).
    /// </summary>
    public IReadOnlyList<MapShapeAutomationPeer> Children(int? folder)
    {
        if (_map.Readable is not { } drawing)
        {
            return [];
        }

        Follow(drawing);

        if ((folder is { } inside ? _held.GetValueOrDefault(inside) : _top) is { } known)
        {
            return known;
        }

        var shapes = folder is { } listed ? _map.ListedIn(listed) : _map.ShapesIn(null);
        var peers = new MapShapeAutomationPeer[shapes.Count];

        for (var i = 0; i < shapes.Count; i++)
        {
            peers[i] = Peer(drawing, folder, shapes[i], i + 1, shapes.Count);
        }

        if (folder is { } held)
        {
            _held[held] = peers;
        }
        else
        {
            _top = peers;
        }

        return peers;
    }

    /// <summary>The peer of the shape <paramref name="node"/> in <paramref name="folder"/>, or null where the drawing on screen does not list it.</summary>
    public MapShapeAutomationPeer? PeerOf(int? folder, int node)
    {
        if (_map.Readable is not { } drawing)
        {
            return null;
        }

        Follow(drawing);

        // Made already for this drawing, which is the usual answer: the folder holding a shape is made
        // before the shape, because a reader reaches a shape through it.
        if (_peers.TryGetValue((folder, node), out var made))
        {
            return made;
        }

        var shapes = _map.ShapesIn(folder);

        for (var i = 0; i < shapes.Count; i++)
        {
            if (shapes[i].Node == node)
            {
                return Peer(drawing, folder, shapes[i], i + 1, shapes.Count);
            }
        }

        return null;
    }

    /// <summary>
    /// The drawing on screen is another one: its shapes are other shapes, or the same ones somewhere
    /// else, and a screen reader is told to read the tree again.
    /// </summary>
    public void Redrawn()
    {
        Forget(_map.Readable);
        _listedFor = _map.KeyboardFolder;

        if (ListenerExists(AutomationEvents.StructureChanged))
        {
            RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, null);
        }
    }

    /// <summary>
    /// The keyboard is on a shape, perhaps in another folder: the folders on its path are listed now,
    /// and those it left are not unless the reader expanded them, so the tree is read again.
    /// </summary>
    public void KeyboardMoved()
    {
        if (_map.KeyboardFolder == _listedFor)
        {
            return;
        }

        _listedFor = _map.KeyboardFolder;
        Relisted();

        if (ListenerExists(AutomationEvents.StructureChanged))
        {
            RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, null);
        }
    }

    /// <summary>
    /// Which folders are listed changed, so each folder's children are found again. The peers stay, so a
    /// screen reader holding one still has the same one.
    /// </summary>
    public void Relisted()
    {
        _held.Clear();
    }

    public IRawElementProviderSimple[] GetSelection() =>
        [.. _map.PickedShapes().Select(picked => PeerOf(picked.Folder, picked.Node)).OfType<MapShapeAutomationPeer>().Select(ProviderFromPeer)];

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;

    protected override string GetClassNameCore() => nameof(ExploreMap);

    /// <summary>The shapes at the top of the picture, then the controls the map holds, such as the overview's buttons.</summary>
    protected override IList<AutomationPeer> GetChildrenCore() =>
        [.. Children(null), .. base.GetChildrenCore() ?? []];

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    /// <summary>The shape the keyboard is on, while the map has the focus, so a screen reader reads it rather than the map.</summary>
    protected override object? GetFocusedElementCore() =>
        _map.FocusedShape() is { } focused && PeerOf(focused.Folder, focused.Node) is { } peer
            ? peer
            : base.GetFocusedElementCore();

    /// <summary>A peer made for this drawing already describes it, because the drawing never changes.</summary>
    private MapShapeAutomationPeer Peer(ExploreSurface drawing, int? folder, MapShape shape, int position, int count)
    {
        if (_peers.TryGetValue((folder, shape.Node), out var made))
        {
            return made;
        }

        var peer = new MapShapeAutomationPeer(this, _map, folder);
        _peers[(folder, shape.Node)] = peer;
        peer.Describe(drawing, shape, position, count);

        // A folder's shapes are under the folder's own, which is at the top of the picture or under
        // its own folder in turn.
        peer.SetParent(folder is { } inside && _map.FolderOf(inside) is var above && PeerOf(above, inside) is { } holder ? holder : this);

        return peer;
    }

    /// <summary>Start again on <paramref name="drawing"/> where what is kept is of another.</summary>
    private void Follow(ExploreSurface drawing)
    {
        if (!ReferenceEquals(_drawn, drawing))
        {
            Forget(drawing);
        }
    }

    private void Forget(ExploreSurface? drawing)
    {
        _peers.Clear();
        _held.Clear();
        _top = null;
        _drawn = drawing;
    }
}
