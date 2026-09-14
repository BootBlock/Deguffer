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
/// <para>What it costs is one pass over what arrived, one over the rows, and then how far the rows
/// actually travelled. Searching for a row that has moved starts where it would be if nothing had
/// moved, so a list in the same order as the last reading costs nothing beyond the two passes. It
/// is quadratic only where most of the list has changed places, which is a different list rather
/// than this one changed, and a caller with one of those clears instead (G4).</para>
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
    {
        ArgumentNullException.ThrowIfNull(arriving);
        ArgumentNullException.ThrowIfNull(keyOfRow);
        ArgumentNullException.ThrowIfNull(keyOfItem);
        ArgumentNullException.ThrowIfNull(make);

        var keys = EqualityComparer<TKey>.Default;
        var here = new HashSet<TKey>(arriving.Count, keys);

        for (var at = 0; at < arriving.Count; at++)
        {
            here.Add(keyOfItem(arriving[at]));
        }

        // Backwards, so a removal never moves a row this pass has still to look at. This is the
        // whole point of the type: what has gone goes now, in one change each, rather than being
        // shuffled to the end of the list a place at a time by the pass below.
        for (var at = rows.Count - 1; at >= 0; at--)
        {
            if (!here.Contains(keyOfRow(rows[at])))
            {
                rows.RemoveAt(at);
            }
        }

        for (var at = 0; at < arriving.Count; at++)
        {
            var item = arriving[at];
            var key = keyOfItem(item);

            if (at < rows.Count && keys.Equals(keyOfRow(rows[at]), key))
            {
                keep(rows, at, item);
            }
            else if (Sits(rows, keyOfRow, keys, key, at) is { } moved)
            {
                rows.Move(moved, at);
                keep(rows, at, item);
            }
            else
            {
                rows.Insert(at, make(item));
            }
        }

        // The pass above places one row per arriving thing, and the pass before it removed whatever
        // was not arriving, so this is reached only where the list held one key twice — which a
        // reading gives where the machine will not say enough to tell two things apart. The list
        // then ends holding what arrived rather than what it started with.
        while (rows.Count > arriving.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    /// <summary>
    /// Where the row for <paramref name="key"/> sits at or after <paramref name="from"/>, or null
    /// where the list holds no row for it. Everything before <paramref name="from"/> is settled, so
    /// the search starts there.
    /// </summary>
    private static int? Sits<TRow, TKey>(
        ObservableCollection<TRow> rows,
        Func<TRow, TKey> keyOfRow,
        IEqualityComparer<TKey> keys,
        TKey key,
        int from)
    {
        for (var at = from; at < rows.Count; at++)
        {
            if (keys.Equals(keyOfRow(rows[at]), key))
            {
                return at;
            }
        }

        return null;
    }
}
