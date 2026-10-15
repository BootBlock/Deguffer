namespace Deguffer.Core.Scanning;

/// <summary>
/// One worker's waiting work: the worker takes the newest from the top, and any other worker that
/// has run out takes the oldest from the bottom.
///
/// <para>The owner working from the top is what makes a walk depth-first: a directory's children
/// are read before anything stacked ahead of them, so each path is used soon after it is built, and
/// what waits is one level's siblings for each level of the current branch rather than a whole level
/// of the tree. A thief working from the bottom takes the shallowest directory waiting, which holds
/// the most below it, so each steal tends to move a large subtree to a worker of its own.</para>
///
/// <para>A lock rather than a lock-free deque. The owner is the only one that pushes, a thief only
/// arrives once its own stack is empty, and each take is followed by a directory listing that costs
/// far more than an uncontended lock, so contention on it should be rare.</para>
/// </summary>
internal sealed class WorkStealingStack<T>
{
    private readonly Lock _lock = new();

    // A ring, so a steal from the bottom moves nothing: _bottom is the oldest item's index, and the
    // length is always a power of two so an index wraps with a mask.
    private T[] _items = new T[16];
    private int _bottom;
    private int _count;

    /// <summary>Whether the stack is empty. Read without the lock, so only a hint.</summary>
    public bool IsEmpty => Volatile.Read(ref _count) == 0;

    /// <summary>Push <paramref name="items"/> so that the first of them is the next taken from the top.</summary>
    public void PushAll(List<T> items)
    {
        lock (_lock)
        {
            while (_count + items.Count > _items.Length)
            {
                Grow();
            }

            for (var i = items.Count - 1; i >= 0; i--)
            {
                _items[(_bottom + _count) & (_items.Length - 1)] = items[i];
                _count++;
            }
        }
    }

    /// <summary>Take the newest item. The owner only.</summary>
    public bool TryPop(out T item)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                item = default!;
                return false;
            }

            _count--;
            item = Take((_bottom + _count) & (_items.Length - 1));
            return true;
        }
    }

    /// <summary>Take the oldest item. Any worker but the owner.</summary>
    public bool TrySteal(out T item)
    {
        if (IsEmpty)
        {
            item = default!;
            return false;
        }

        lock (_lock)
        {
            if (_count == 0)
            {
                item = default!;
                return false;
            }

            item = Take(_bottom);
            _bottom = (_bottom + 1) & (_items.Length - 1);
            _count--;
            return true;
        }
    }

    /// <summary>
    /// The item at <paramref name="index"/>, with its slot cleared: a taken path left in the array
    /// would live as long as the stack, which is what a depth-first walk is meant to stop.
    /// </summary>
    private T Take(int index)
    {
        var item = _items[index];
        _items[index] = default!;
        return item;
    }

    private void Grow()
    {
        var grown = new T[_items.Length * 2];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _items[(_bottom + i) & (_items.Length - 1)];
        }

        _items = grown;
        _bottom = 0;
    }
}
