using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;

namespace Deguffer.App.Controls;

/// <summary>
/// One shape on the map as a screen reader reads it: its name, its size and share of what holds it,
/// what it is and what can be done with it (<see cref="Core.Exploring.MapShapeText"/>), where it is on
/// the screen, and the shapes it holds under it, while it is expanded.
///
/// <para>Acts as the keyboard does. Invoking it opens it, as Enter does; selecting it picks it, as a
/// click does, so Delete and the menu then ask the same questions of it as of a shape picked by the
/// pointer (§7.1); and scrolling it into view brings the camera to it. Expanding it lists its shapes
/// under it, as a tree view's item does, and it is expanded already where the keyboard's shape is
/// inside it (<see cref="MapFolders.Lists"/>).</para>
/// </summary>
internal sealed partial class MapShapeAutomationPeer : AutomationPeer, IInvokeProvider, ISelectionItemProvider, IScrollItemProvider, IExpandCollapseProvider
{
    private readonly MapAutomationPeer _owner;

    private readonly ExploreMap _map;

    private readonly int? _folder;

    private MapShape _shape;

    /// <summary>
    /// The drawing the shape is of. Everything the peer does is asked of it, and refused once another
    /// drawing is on screen, whose node numbers may be other nodes (§7.1).
    /// </summary>
    private ExploreSurface? _drawing;

    private int _position;

    private int _count;

    public MapShapeAutomationPeer(MapAutomationPeer owner, ExploreMap map, int? folder)
    {
        _owner = owner;
        _map = map;
        _folder = folder;
    }

    public bool IsSelected => Current && _shape.IsNode && _map.IsPicked(_shape.Node);

    public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(_owner);

    /// <summary>Whether the drawing the shape is of is the one on screen.</summary>
    private bool Current => _drawing is not null && ReferenceEquals(_drawing, _map.Readable);

    /// <summary>Where the shape is in <paramref name="drawing"/>, and which of its folder's shapes it is.</summary>
    public void Describe(ExploreSurface drawing, MapShape shape, int position, int count)
    {
        _drawing = drawing;
        _shape = shape;
        _position = position;
        _count = count;
    }

    public void Invoke()
    {
        if (_drawing is { } drawing)
        {
            _map.OpenShape(drawing, _folder, _shape);
        }
    }

    public void Select()
    {
        if (_drawing is { } drawing)
        {
            _map.PickShape(drawing, _folder, _shape);
        }
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
        if (IsSelected)
        {
            _map.Unpick();
        }
    }

    public void ScrollIntoView()
    {
        if (_drawing is { } drawing)
        {
            _map.BringIntoView(drawing, _shape);
        }
    }

    /// <summary>
    /// A leaf where the drawing draws nothing in the shape, which is a file, a block, or a folder drawn
    /// as one block; otherwise whether its shapes are listed under it.
    /// </summary>
    public ExpandCollapseState ExpandCollapseState =>
        !Current || !_shape.IsNode || _map.ShapesIn(_shape.Node).Count == 0 ? ExpandCollapseState.LeafNode
        : _map.Lists(_shape.Node) ? ExpandCollapseState.Expanded
        : ExpandCollapseState.Collapsed;

    public void Expand()
    {
        if (ExpandCollapseState == ExpandCollapseState.Collapsed && _map.Expand(_shape.Node))
        {
            Restructured(ExpandCollapseState.Collapsed);
        }
    }

    /// <summary>
    /// Where the keyboard is on a shape inside it, the keyboard comes up to this one first, as a tree
    /// view's does, so the shape it is on stays in the tree. While the map does not have the focus, the
    /// keyboard's place is left where it is, and the folders on its path stay listed.
    /// </summary>
    public void Collapse()
    {
        if (!Current || !_shape.IsNode || _drawing is not { } drawing)
        {
            return;
        }

        var was = ExpandCollapseState;

        _map.Collapse(_shape.Node);

        if (_map.Lists(_shape.Node) && _map.FocusedShape() is not null)
        {
            _map.FocusShape(drawing, _folder, _shape);
        }

        if (was == ExpandCollapseState.Expanded && ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            Restructured(was);
        }
    }

    protected override string GetNameCore() => Current ? _map.NameOf(_folder, _shape) : string.Empty;

    protected override string GetHelpTextCore() => Current ? _map.HelpOf(_shape) : string.Empty;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    protected override string GetClassNameCore() => "MapShape";

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() => Current && _map.HasFocusOn(_folder, _shape.Node);

    protected override void SetFocusCore()
    {
        if (_drawing is { } drawing)
        {
            _map.FocusShape(drawing, _folder, _shape);
        }
    }

    protected override bool IsOffscreenCore() => !Current || _map.OnScreenOf(_shape) is not { } frame
        || frame.X + frame.Width <= 0 || frame.Y + frame.Height <= 0 || frame.X >= 1 || frame.Y >= 1;

    /// <summary>
    /// The part of the map's own rectangle the shape covers on the screen, cut to the map, so it is in
    /// whatever units a screen reader is given the map's in.
    /// </summary>
    protected override Rect GetBoundingRectangleCore()
    {
        if (!Current || _map.OnScreenOf(_shape) is not { } frame)
        {
            return default;
        }

        var map = _owner.GetBoundingRectangle();
        var shown = frame.Clipped(MapFrame.Whole);

        return new Rect(
            map.X + (shown.X * map.Width),
            map.Y + (shown.Y * map.Height),
            Math.Max(0, shown.Width * map.Width),
            Math.Max(0, shown.Height * map.Height));
    }

    protected override IList<AutomationPeer>? GetChildrenCore() =>
        Current && _shape.IsNode && _map.Lists(_shape.Node) ? [.. _owner.Children(_shape.Node)] : null;

    protected override int GetPositionInSetCore() => _position;

    protected override int GetSizeOfSetCore() => _count;

    protected override object? GetPatternCore(PatternInterface patternInterface) => !Current ? null : patternInterface switch
    {
        PatternInterface.Invoke => this,
        PatternInterface.ScrollItem => this,
        PatternInterface.SelectionItem when _shape.IsNode && _map.Answers => this,
        PatternInterface.ExpandCollapse => this,
        _ => null,
    };

    /// <summary>The shapes under this one changed from <paramref name="was"/>, and a screen reader is told so.</summary>
    private void Restructured(ExpandCollapseState was)
    {
        if (ListenerExists(AutomationEvents.StructureChanged))
        {
            RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, null);
        }

        RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, was, ExpandCollapseState);
    }
}
