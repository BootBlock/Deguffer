using System.Collections.ObjectModel;

namespace Deguffer.Core.Viewing;

/// <summary>
/// Brings a bound list to what has just arrived, in the fewest changes that get it there: one
/// removal for a thing that has gone, one insertion for a thing that is new, one move for a thing
/// that has changed places, and nothing at all for a list that has not changed.
///
/// <para>A bound list is not a value to be reassigned. Clearing it and filling it again raises a
/// reset, and a <c>ListView</c> answers a reset by throwing away every container it holds: the
/// scroll position, the keyboard focus, the selection and whatever the pointer was over go with
/// them. A page that reads the machine every couple of seconds does that to its reader every couple
/// of seconds.</para>
///
/// <para>Nor is a run of moves a substitute for a removal. Sliding a row that has gone down to the
/// end of the list one place at a time costs a move for every row below it, and a list builds a
/// container again for each move it is told about — so one process exiting rebuilds the whole list
/// under it, which is a rebuild by another name. Everything that has gone is therefore removed
/// first, before a single row is placed.</para>
///
/// <para>The same is true of a row that has only changed places, and that one is easier to miss. A
/// pass that walks the arriving order and pulls each wanted row up to the front answers one row
/// sinking twenty places by moving the twenty above it up one each, which is twenty containers
/// rebuilt to avoid rebuilding one. So the rows that are already in the arriving order relative to
/// each other are found first — the longest such run there is — and left alone, and every other
/// staying row is moved exactly once. That is the fewest moves the arriving order can be reached in.
/// Measured against a real machine's process list, it is the difference between thirty moves a
/// reading and four.</para>
///
/// <para>What it costs is a pass over what arrived, a pass over the rows, the longest run in
/// <c>n log n</c>, and then one move per row that is actually out of place. A list in the same order
/// as the last reading costs nothing beyond the passes.</para>
/// </summary>
public static class LiveList
{
    /// <summary>
    /// Show <paramref name="arriving"/> in <paramref name="rows"/>, where a row is an object that
    /// can be written over. A row that is still here keeps its identity, so the list keeps the
    /// container, the selection and the focus that belong to it.
    /// </summary>
    /// <param name="keyOfRow">What a row on screen is about.</param>
    /// <param name="keyOfItem">What an arriving thing is, by the same measure.</param>
    /// <param name="make">A row for a thing the list does not hold yet.</param>
    /// <param name="update">Re-read into a row that is staying whatever this reading changed.</param>
    public static void Show<TRow, TItem, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> arriving,
        Func<TRow, TKey> keyOfRow,
        Func<TItem, TKey> keyOfItem,
        Func<TItem, TRow> make,
        Action<TRow, TItem> update)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(update);

        Settle(rows, arriving, keyOfRow, keyOfItem, make, (list, at, item) => update(list[at], item));
    }

    /// <summary>
    /// The same, where a row is a value rather than something that can be written over. An entry
    /// that is still here by <paramref name="keyOf"/> but no longer equal to what arrived is
    /// replaced where it sits, which costs the one container rather than the list.
    /// </summary>
    public static void Show<TValue, TKey>(
        ObservableCollection<TValue> rows,
        IReadOnlyList<TValue> arriving,
        Func<TValue, TKey> keyOf)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);

        Settle(rows, arriving, keyOf, keyOf, value => value, Replace);

        static void Replace(ObservableCollection<TValue> list, int at, TValue value)
        {
            if (!EqualityComparer<TValue>.Default.Equals(list[at], value))
            {
                list[at] = value;
            }
        }
    }

    /// <summary>
    /// The same, for a list of sentences rather than a list of things: nothing in it has an
    /// identity to match, so an entry that has changed is written over where it sits and the rest
    /// are left alone.
    /// </summary>
    public static void Rewrite<TValue>(ObservableCollection<TValue> rows, IReadOnlyList<TValue> arriving)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(arriving);

        for (var at = 0; at < arriving.Count; at++)
        {
            if (at >= rows.Count)
            {
                rows.Add(arriving[at]);
            }
            else if (!EqualityComparer<TValue>.Default.Equals(rows[at], arriving[at]))
            {
                rows[at] = arriving[at];
            }
        }

        while (rows.Count > arriving.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    /// <param name="keep">
    /// What to do with a row that is staying: write over it, or replace the value where it sits.
    /// It takes the list and the index rather than the row, because replacing is the list's to do.
    /// </param>
    private static void Settle<TRow, TItem, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> arriving,
        Func<TRow, TKey> keyOfRow,
        Func<TItem, TKey> keyOfItem,
        Func<TItem, TRow> make,
        Action<ObservableCollection<TRow>, int, TItem> keep)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(arriving);
        ArgumentNullException.ThrowIfNull(keyOfRow);
        ArgumentNullException.ThrowIfNull(keyOfItem);
        ArgumentNullException.ThrowIfNull(make);

        var keys = EqualityComparer<TKey>.Default;

        Drop(rows, arriving, keyOfRow, keyOfItem, keys);

        // Where the row for each arriving thing sits now, and -1 for one the list does not hold
        // yet. Every row left after Drop is one of these, so the rows are a permutation of the
        // staying part of this.
        var from = Places(rows, arriving, keyOfRow, keyOfItem, keys);
        var anchored = new bool[arriving.Count];

        Anchor(from, anchored);
        Reorder(rows, from, anchored);

        // The staying rows are now in the arriving order, so each new thing goes in at its own
        // place and each row that stayed is already under the thing it is about.
        for (var at = 0; at < arriving.Count; at++)
        {
            if (from[at] < 0)
            {
                rows.Insert(at, make(arriving[at]));
            }
            else
            {
                keep(rows, at, arriving[at]);
            }
        }
    }

    /// <summary>
    /// Remove every row for something that has not arrived, before a single row is placed.
    ///
    /// <para>Counted rather than matched, because a list can hold one key twice where the machine
    /// will not say enough to tell two things apart: three rows for a key that arrived twice leave
    /// two rows rather than none.</para>
    /// </summary>
    private static void Drop<TRow, TItem, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> arriving,
        Func<TRow, TKey> keyOfRow,
        Func<TItem, TKey> keyOfItem,
        IEqualityComparer<TKey> keys)
        where TKey : notnull
    {
        var wanted = new Dictionary<TKey, int>(arriving.Count, keys);

        for (var at = 0; at < arriving.Count; at++)
        {
            var key = keyOfItem(arriving[at]);

            wanted[key] = wanted.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }

        // Backwards, so a removal never moves a row this pass has still to look at.
        for (var at = rows.Count - 1; at >= 0; at--)
        {
            var key = keyOfRow(rows[at]);

            if (wanted.TryGetValue(key, out var left) && left > 0)
            {
                wanted[key] = left - 1;
            }
            else
            {
                rows.RemoveAt(at);
            }
        }
    }

    /// <summary>Where the row for each arriving thing sits, and -1 for one the list does not hold.</summary>
    private static int[] Places<TRow, TItem, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> arriving,
        Func<TRow, TKey> keyOfRow,
        Func<TItem, TKey> keyOfItem,
        IEqualityComparer<TKey> keys)
        where TKey : notnull
    {
        // Where to start looking for each key, so a thing that is new costs a failed lookup rather
        // than a pass over the rows, and a key held twice carries on from the one already taken.
        var cursor = new Dictionary<TKey, int>(rows.Count, keys);

        for (var at = 0; at < rows.Count; at++)
        {
            cursor.TryAdd(keyOfRow(rows[at]), at);
        }

        var from = new int[arriving.Count];

        for (var at = 0; at < arriving.Count; at++)
        {
            from[at] = Sits(rows, keyOfRow, keys, keyOfItem(arriving[at]), cursor);
        }

        return from;
    }

    private static int Sits<TRow, TKey>(
        ObservableCollection<TRow> rows,
        Func<TRow, TKey> keyOfRow,
        IEqualityComparer<TKey> keys,
        TKey key,
        Dictionary<TKey, int> cursor)
        where TKey : notnull
    {
        if (!cursor.TryGetValue(key, out var start))
        {
            return -1;
        }

        for (var at = start; at < rows.Count; at++)
        {
            if (keys.Equals(keyOfRow(rows[at]), key))
            {
                cursor[key] = at + 1;

                return at;
            }
        }

        cursor[key] = rows.Count;

        return -1;
    }

    /// <summary>
    /// Mark the longest run of staying rows that is already in the arriving order relative to each
    /// other. Those rows do not move, and every other staying row moves once, which is the fewest
    /// moves the arriving order can be reached in.
    ///
    /// <para>Patience sorting: <c>tails[length - 1]</c> is the arriving index that ends a run of
    /// that length with the smallest row index it can end on, and <c>came</c> is what precedes each
    /// index in the run that ends there, so the run itself can be read back from the last one.</para>
    /// </summary>
    private static void Anchor(int[] from, bool[] anchored)
    {
        var tails = new List<int>();
        var came = new int[from.Length];

        for (var at = 0; at < from.Length; at++)
        {
            came[at] = -1;

            if (from[at] < 0)
            {
                continue;
            }

            var low = 0;
            var high = tails.Count;

            while (low < high)
            {
                var middle = (low + high) / 2;

                if (from[tails[middle]] < from[at])
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            came[at] = low > 0 ? tails[low - 1] : -1;

            if (low == tails.Count)
            {
                tails.Add(at);
            }
            else
            {
                tails[low] = at;
            }
        }

        for (var at = tails.Count > 0 ? tails[^1] : -1; at >= 0; at = came[at])
        {
            anchored[at] = true;
        }
    }

    /// <summary>
    /// Put every staying row that is out of place where it belongs, one move each.
    ///
    /// <para>A row goes immediately before the next row that is not moving, which is what puts it
    /// between the two rows it belongs between however far it has travelled and in whichever
    /// direction. The rows that are not moving are already in the arriving order relative to each
    /// other, and a row placed in arriving order lands after the ones placed before it, so the
    /// settled rows stay in that order as the pass goes on.</para>
    /// </summary>
    private static void Reorder<TRow>(ObservableCollection<TRow> rows, int[] from, bool[] anchored)
    {
        // The next row that is not moving, per arriving index, or -1 where the rest all move: the
        // last of those goes to the end.
        var next = new int[from.Length];
        var ahead = -1;

        for (var at = from.Length - 1; at >= 0; at--)
        {
            next[at] = ahead;

            if (anchored[at])
            {
                ahead = at;
            }
        }

        // Where each row sits as the moves are made, which is what from says before any of them.
        var slot = (int[])from.Clone();

        for (var at = 0; at < from.Length; at++)
        {
            if (from[at] < 0 || anchored[at])
            {
                continue;
            }

            var moving = slot[at];

            // Removing the row first shifts everything below it up one, so a row travelling down
            // lands one place short of where the row it goes before sits now.
            var before = next[at] < 0 ? rows.Count : slot[next[at]];
            var to = moving < before ? before - 1 : before;

            if (moving == to)
            {
                continue;
            }

            rows.Move(moving, to);
            Shift(slot, moving, to);
        }
    }

    /// <summary>Where every row sits once one of them has moved from <paramref name="moved"/> to <paramref name="to"/>.</summary>
    private static void Shift(int[] slot, int moved, int to)
    {
        for (var at = 0; at < slot.Length; at++)
        {
            var place = slot[at];

            slot[at] = place switch
            {
                _ when place == moved => to,
                _ when moved < to && place > moved && place <= to => place - 1,
                _ when moved > to && place >= to && place < moved => place + 1,
                _ => place,
            };
        }
    }
}
