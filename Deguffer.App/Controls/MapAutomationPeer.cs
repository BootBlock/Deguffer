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
/// </summary>
internal sealed partial class MapAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
{
    private readonly ExploreMap _map;

    /// <summary>The peers made for the drawing in <see cref="_drawn"/>, by folder and shape.</summary>
    private readonly Dictionary<(int? Folder, int Node), MapShapeAutomationPeer> _peers = [];

    private object? _drawn;

    public MapAutomationPeer(ExploreMap map)
        : base(map)
    {
        _map = map;
    }

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    /// <summary>
    /// The peers of <paramref name="folder"/>'s shapes, or the top of the picture's for null, as the
    /// drawing on screen draws them.
    /// </summary>
    public IReadOnlyList<MapShapeAutomationPeer> Children(int? folder)
    {
        if (_map.Readable is not { } drawing)
        {
            return [];
        }

        var shapes = _map.ShapesIn(folder);
        var peers = new MapShapeAutomationPeer[shapes.Count];

        for (var i = 0; i < shapes.Count; i++)
        {
            peers[i] = Peer(drawing, folder, shapes[i], i + 1, shapes.Count);
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
        _peers.Clear();
        _drawn = _map.Readable;

        if (ListenerExists(AutomationEvents.StructureChanged))
        {
            RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, null);
        }
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

    private MapShapeAutomationPeer Peer(ExploreSurface drawing, int? folder, MapShape shape, int position, int count)
    {
        if (!ReferenceEquals(_drawn, drawing))
        {
            _peers.Clear();
            _drawn = drawing;
        }

        if (!_peers.TryGetValue((folder, shape.Node), out var peer))
        {
            peer = new MapShapeAutomationPeer(this, _map, folder);
            _peers[(folder, shape.Node)] = peer;

            // A folder's shapes are under the folder's own, which is at the top of the picture or under
            // its own folder in turn.
            peer.SetParent(folder is { } inside && _map.FolderOf(inside) is var above && PeerOf(above, inside) is { } holder ? holder : this);
        }

        peer.Describe(drawing, shape, position, count);

        return peer;
    }
}
