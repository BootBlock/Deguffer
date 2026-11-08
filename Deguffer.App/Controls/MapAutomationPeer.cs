using Deguffer.Core.Exploring.Layout;
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
/// changed. What the tree says of a drawing is its <see cref="MapReader"/>'s.</para>
///
/// <para>A folder's shapes are in the tree only along the keyboard's path and where the reader expanded
/// the folder (<see cref="MapFolders.Lists"/>), so the tree is the size of what the reader reached
/// rather than of the picture. Which folders the reader expanded is kept here, across drawings.</para>
/// </summary>
internal sealed partial class MapAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
{
    private readonly ExploreMap _map;

    /// <summary>
    /// The folders a screen reader expanded on this picture, which list their shapes as the keyboard's
    /// path does (<see cref="MapFolders.Lists"/>). Carried with the keyboard to a new tree of the same
    /// place (<see cref="Carry"/>).
    /// </summary>
    private readonly HashSet<int> _expanded = [];

    /// <summary>The peers made for the drawing <see cref="_reader"/> reads, by folder and shape.</summary>
    private readonly Dictionary<(int? Folder, int Node), MapShapeAutomationPeer> _peers = [];

    /// <summary>
    /// Each folder's peers in order, for the drawing <see cref="_reader"/> reads, once asked. A search of
    /// the window asks for a folder's children at each step through them.
    /// </summary>
    private readonly Dictionary<int, IReadOnlyList<MapShapeAutomationPeer>> _held = [];

    /// <summary>The peers at the top of the picture, as <see cref="_held"/> keeps a folder's.</summary>
    private IReadOnlyList<MapShapeAutomationPeer>? _top;

    /// <summary>The reader of the drawing whose peers are kept.</summary>
    private MapReader? _reader;

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
        if (_map.Reader is not { } reader)
        {
            return [];
        }

        Follow(reader);

        if ((folder is { } inside ? _held.GetValueOrDefault(inside) : _top) is { } known)
        {
            return known;
        }

        var shapes = folder is { } listed ? reader.Listed(listed, _expanded) : reader.Shapes(null);
        var peers = new MapShapeAutomationPeer[shapes.Count];

        // What the new ones go under, found once for all of them, and only where one of them is new.
        AutomationPeer? holder = null;

        for (var i = 0; i < shapes.Count; i++)
        {
            peers[i] = _peers.GetValueOrDefault((folder, shapes[i].Node))
                ?? Make(reader, folder, shapes[i], i + 1, shapes.Count, holder ??= HolderOf(reader, folder));
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

    /// <summary>
    /// The sibling one step from <paramref name="peer"/>, which is at <paramref name="index"/> among
    /// <paramref name="folder"/>'s shapes. See <see cref="MapTreeSteps.Sibling"/>. After the last shape at
    /// the top of the picture come the controls the map holds, as <see cref="GetChildrenCore"/> gives them.
    /// </summary>
    public AutomationPeer? SiblingOf(MapShapeAutomationPeer peer, int? folder, int index, bool forward) =>
        MapTreeSteps.Sibling<AutomationPeer>(Children(folder), index, peer, forward, folder is null ? FirstHeld : static () => null);

    /// <summary>The peer of the shape <paramref name="node"/> in <paramref name="folder"/>, or null where the drawing on screen does not list it.</summary>
    public MapShapeAutomationPeer? PeerOf(int? folder, int node)
    {
        if (_map.Reader is not { } reader)
        {
            return null;
        }

        Follow(reader);

        // Made already for this drawing, which is the usual answer: the folder holding a shape is made
        // before the shape, because a reader reaches a shape through it.
        if (_peers.TryGetValue((folder, node), out var made))
        {
            return made;
        }

        var shapes = reader.Shapes(folder);

        return MapShapes.IndexOf(shapes, node) is { } index
            ? Make(reader, folder, shapes[index], index + 1, shapes.Count, HolderOf(reader, folder))
            : null;
    }

    /// <summary>
    /// The drawing on screen is another one: its shapes are other shapes, or the same ones somewhere
    /// else, and a screen reader is told to read the tree again.
    /// </summary>
    /// <param name="keyboard">The folder whose shapes the keyboard moves between, or null for the top of the picture.</param>
    public void Redrawn(int? keyboard)
    {
        Forget(_map.Reader);
        _listedFor = keyboard;

        if (ListenerExists(AutomationEvents.StructureChanged))
        {
            RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, null);
        }
    }

    /// <summary>
    /// The keyboard is on a shape, perhaps in another folder: the folders on its path are listed now,
    /// and those it left are not unless the reader expanded them, so the tree is read again.
    /// </summary>
    /// <param name="keyboard">The folder whose shapes the keyboard moves between now, or null for the top of the picture.</param>
    public void KeyboardMoved(int? keyboard)
    {
        if (keyboard == _listedFor)
        {
            return;
        }

        _listedFor = keyboard;
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

    /// <summary>Whether <paramref name="reader"/> gives <paramref name="folder"/>'s shapes, along the keyboard's path or because it was expanded.</summary>
    public bool Lists(MapReader reader, int folder) => reader.Lists(folder, _expanded);

    /// <summary>A screen reader expanded <paramref name="folder"/>. False where it was already.</summary>
    public bool Expand(int folder) => _expanded.Add(folder);

    /// <summary>A screen reader collapsed <paramref name="folder"/>. False where it had not expanded it.</summary>
    public bool Collapse(int folder) => _expanded.Remove(folder);

    /// <summary>
    /// The keyboard went to a new tree: the folders expanded go with it to a tree of the same place, read
    /// through their <paramref name="counterpart"/>s, and are forgotten where there is none, in another
    /// place, whose folders a reader has not expanded yet. Where the keyboard goes is
    /// <see cref="ExploreKeyboard.Carry"/>'s to say.
    /// </summary>
    public void Carry(Func<int, int?>? counterpart)
    {
        int[] expanded = counterpart is null ? [] : [.. _expanded.Select(counterpart).OfType<int>()];

        _expanded.Clear();
        _expanded.UnionWith(expanded);
    }

    public IRawElementProviderSimple[] GetSelection() =>
        _map.Reader is { } reader
            ? [.. reader.PickedShapes().Select(picked => PeerOf(picked.Folder, picked.Node)).OfType<MapShapeAutomationPeer>().Select(ProviderFromPeer)]
            : [];

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;

    protected override string GetClassNameCore() => nameof(ExploreMap);

    /// <summary>The shapes at the top of the picture, then the controls the map holds, such as the overview's buttons.</summary>
    protected override IList<AutomationPeer> GetChildrenCore() =>
        [.. Children(null), .. base.GetChildrenCore() ?? []];

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    /// <summary>The shape the keyboard is on, while the map has the focus, so a screen reader reads it rather than the map.</summary>
    protected override object? GetFocusedElementCore() =>
        _map.Reader?.FocusedShape() is { } focused && PeerOf(focused.Folder, focused.Node) is { } peer
            ? peer
            : base.GetFocusedElementCore();

    /// <summary>The first of the controls the map holds, which follow its shapes in the tree.</summary>
    private AutomationPeer? FirstHeld() => base.GetChildrenCore() is [var first, ..] ? first : null;

    /// <summary>
    /// A peer of <paramref name="shape"/>, under <paramref name="holder"/>, kept for as long as the drawing
    /// is the one read: it never changes, so the peer goes on describing it.
    /// </summary>
    private MapShapeAutomationPeer Make(MapReader reader, int? folder, MapShape shape, int position, int count, AutomationPeer holder)
    {
        var peer = new MapShapeAutomationPeer(this, reader, folder, shape, position, count);
        _peers[(folder, shape.Node)] = peer;
        peer.SetParent(holder);

        return peer;
    }

    /// <summary>
    /// What a peer of <paramref name="folder"/>'s shapes is under: the folder's own shape, which is at the
    /// top of the picture or under its own folder in turn; or the map, at the top of the picture.
    /// </summary>
    private AutomationPeer HolderOf(MapReader reader, int? folder) =>
        folder is { } inside && PeerOf(reader.FolderOf(inside), inside) is { } holder ? holder : this;

    /// <summary>Start again on <paramref name="reader"/>'s drawing where what is kept is of another.</summary>
    private void Follow(MapReader reader)
    {
        if (!ReferenceEquals(_reader, reader))
        {
            Forget(reader);
        }
    }

    private void Forget(MapReader? reader)
    {
        _peers.Clear();
        _held.Clear();
        _top = null;
        _reader = reader;
    }
}
