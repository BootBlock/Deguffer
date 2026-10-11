using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The tiles of a map's picture drawn so far, kept while they fit in a budget of bytes and let go least
/// recently used first, so zooming back out, or panning back, shows sharp tiles at once.
///
/// <para>Counted in bytes rather than tiles, because a tile at the picture's edge is smaller than the
/// rest, and what runs out is memory. The budget is never exceeded: a tile added past it lets go of as
/// many of the least recently used as it takes, before it is kept.</para>
///
/// <para>Generic in what is kept for a tile, because what holds a drawn tile is the shell's (a surface
/// and the visual it is shown in), and which tiles to keep is a decision with no window in it (G1).</para>
/// </summary>
/// <typeparam name="T">What is kept for each tile.</typeparam>
public sealed class MapTileCache<T>
{
    private readonly Dictionary<MapTileKey, LinkedListNode<Entry>> _entries = [];

    /// <summary>Least recently used first.</summary>
    private readonly LinkedList<Entry> _order = [];

    private readonly Action<T> _evicted;

    /// <param name="budget">How many bytes of tiles to keep at most.</param>
    /// <param name="evicted">Told of each tile let go, so what holds it can be used again (G5).</param>
    public MapTileCache(long budget, Action<T> evicted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        ArgumentNullException.ThrowIfNull(evicted);

        Budget = budget;
        _evicted = evicted;
    }

    public long Budget { get; }

    /// <summary>How many bytes the tiles kept hold.</summary>
    public long Bytes { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Every tile kept, in no particular order.</summary>
    public IEnumerable<T> Values => _order.Select(entry => entry.Value);

    /// <summary>Whether <paramref name="key"/> is kept. Asking does not count as using it.</summary>
    public bool Contains(MapTileKey key) => _entries.ContainsKey(key);

    /// <summary>What is kept for <paramref name="key"/>, which counts as using it; false where it is not kept.</summary>
    public bool TryGet(MapTileKey key, out T value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }

        _order.Remove(node);
        _order.AddLast(node);

        value = node.Value.Value;
        return true;
    }

    /// <summary>
    /// Keep <paramref name="value"/> for <paramref name="key"/>, <paramref name="bytes"/> large, as the
    /// most recently used, letting go of the least recently used until it fits. A tile already kept for
    /// the key is let go first.
    /// </summary>
    public void Add(MapTileKey key, T value, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, Budget);

        if (_entries.TryGetValue(key, out var kept))
        {
            Evict(kept);
        }

        while (Bytes + bytes > Budget)
        {
            Evict(_order.First!);
        }

        _entries[key] = _order.AddLast(new Entry(key, value, bytes));
        Bytes += bytes;
    }

    /// <summary>Let go of every tile.</summary>
    public void Clear()
    {
        while (_order.First is { } first)
        {
            Evict(first);
        }
    }

    private void Evict(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _entries.Remove(node.Value.Key);
        Bytes -= node.Value.Bytes;

        _evicted(node.Value.Value);
    }

    private readonly record struct Entry(MapTileKey Key, T Value, long Bytes);
}
