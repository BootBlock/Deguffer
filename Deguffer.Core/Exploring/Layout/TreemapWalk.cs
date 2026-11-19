using static Deguffer.Core.Exploring.Layout.TreemapLayout;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One picture's layout kept to the folders above one shape: each laid out as the whole picture lays
/// it out, and nothing beside them opened. Its window is the whole picture, so nothing on the way is
/// passed over for being off the canvas. What <see cref="TreemapLayout.Locate"/> and
/// <see cref="TreemapLayout.Within"/> walk.
/// </summary>
internal sealed class TreemapWalk : IDisposable
{
    private readonly ISizedTree _tree;
    private readonly int _root;
    private readonly float _width;
    private readonly float _height;
    private readonly LayoutLimits _limits;
    private readonly VolumeSpace _volume;
    private readonly Rectangle _picture;
    private readonly ShapeBuffer _dropped = new();

    public TreemapWalk(ISizedTree tree, int root, float width, float height, LayoutLimits limits, VolumeSpace volume, MapViewport viewport)
    {
        _tree = tree;
        _root = root;
        _width = width;
        _height = height;
        _limits = limits;
        _volume = volume;
        _picture = new Rectangle(
            -viewport.Left * width * viewport.Zoom,
            -viewport.Top * height * viewport.Zoom,
            width * viewport.Zoom,
            height * viewport.Zoom);

        Detail = new TreemapDetail(limits, viewport);
        Bounds = new Bounds(new Rectangle(0, 0, width, height), _picture);
    }

    public TreemapDetail Detail { get; }

    public Bounds Bounds { get; }

    /// <summary>The blocks of the volume laid beside the root by the last walk.</summary>
    public ShapeBuffer Beside { get; } = new();

    /// <summary>
    /// Where <paramref name="node"/>'s own shape is laid, how deep it is, and the level of detail
    /// the folder holding it was laid at; or null where it is not under the root, or the picture
    /// draws it inside a shape rather than as one of its own.
    /// </summary>
    public (Rectangle Frame, int Depth, int Level)? To(int node)
    {
        var path = new HashSet<int>();

        for (var at = node; path.Add(at) && at != _root; at = _tree.ParentOf(at))
        {
            if (at == _tree.ParentOf(at))
            {
                return null;
            }
        }

        if (_tree.ChildOrder != ExploreChildOrder.BySize || _width <= 0 || _height <= 0 || _tree.SizeOf(_root) <= 0)
        {
            return null;
        }

        Beside.Clear();

        // The shapes laid beside the path are laid and dropped.
        _dropped.Clear();
        var pending = new Stack<(int Node, int Depth, Rectangle Frame, int Level)>();
        pending.Push((_root, 0, BesideTheVolume(_tree.SizeOf(_root), _volume, _picture, Bounds, _limits, Beside), 0));

        while (pending.TryPop(out var next))
        {
            var (at, depth, frame, from) = next;

            if (!path.Contains(at))
            {
                continue;
            }

            if (at == node)
            {
                return (frame, depth, from);
            }

            var level = Detail.OpeningLevel(_tree, at, depth, frame.Width, frame.Height, from);

            if (level < 0)
            {
                return null;
            }

            var (header, gap) = Detail.FrameOf(frame.Width, frame.Height, level);
            var top = header > 0 ? header : gap;
            var area = new Rectangle(
                frame.X + gap, frame.Y + top,
                frame.Width - (gap * 2), frame.Height - top - gap);

            Place(_tree, at, depth + 1, area, level, Detail, Bounds, _dropped, pending);
        }

        return null;
    }

    public void Dispose()
    {
        Beside.Dispose();
        _dropped.Dispose();
    }
}
