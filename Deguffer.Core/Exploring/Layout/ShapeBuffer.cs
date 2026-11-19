using System.Buffers;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The shapes a layout has laid so far, in an array rented from the shared pool, until the layout
/// copies them once into an array of exactly their number.
///
/// <para>A layout runs once per drawing and once per tile of the map, and cannot know how many shapes
/// it will lay until it has laid them. A list grown to that number copies every shape at each doubling,
/// and past about 1,770 shapes each of its backing arrays and the copy made from it land on the large
/// object heap, with the full collections that follow. Rented, the growth is paid once and reused by
/// every drawing after it, and the one array a drawing keeps is the exact one it returns (G4).</para>
/// </summary>
internal sealed class ShapeBuffer : IDisposable
{
    // Enough for a small picture without growing, and still far below the large object heap.
    private const int FirstLength = 256;

    private ExploreTile[] _shapes = [];

    public int Count { get; private set; }

    /// <summary>The shapes laid so far, in the order they were laid.</summary>
    public ReadOnlySpan<ExploreTile> Shapes => _shapes.AsSpan(0, Count);

    public void Add(ExploreTile shape)
    {
        if (Count == _shapes.Length)
        {
            Grow();
        }

        _shapes[Count++] = shape;
    }

    public void Clear() => Count = 0;

    /// <summary>The shapes laid so far, in an array of exactly their number that the caller keeps.</summary>
    public ExploreTile[] ToArray() => Shapes.ToArray();

    public void Dispose()
    {
        Return();
        _shapes = [];
        Count = 0;
    }

    private void Grow()
    {
        var larger = ArrayPool<ExploreTile>.Shared.Rent(Math.Max(FirstLength, _shapes.Length * 2));
        Shapes.CopyTo(larger);
        Return();
        _shapes = larger;
    }

    // ExploreTile holds no references, so a returned array needs no clearing for the collector's sake.
    private void Return()
    {
        if (_shapes.Length > 0)
        {
            ArrayPool<ExploreTile>.Shared.Return(_shapes);
        }
    }
}
