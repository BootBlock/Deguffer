namespace Deguffer.Core.Viewing;

/// <summary>What one row on screen does as the list it is in changes. A row that does nothing has none.</summary>
public enum RowMove
{
    /// <summary>Glides into its place from <see cref="RowMotion{TKey}.From"/> above or below it.</summary>
    Glide,

    /// <summary>New to the list: fades in, and slides in where the motion travels.</summary>
    Arrive,

    /// <summary>Gone from the list: fades out where it stood, at <see cref="RowMotion{TKey}.From"/>.</summary>
    Leave,
}

/// <summary>What one row does, and from where.</summary>
/// <param name="Key">The row.</param>
/// <param name="Move">What it does.</param>
/// <param name="From">
/// For a glide, how far below its place it starts (negative is above). For a row leaving, the top it
/// stood at. Zero otherwise.
/// </param>
public readonly record struct RowMotion<TKey>(TKey Key, RowMove Move, double From);

/// <summary>How a list on screen shows one change to it.</summary>
/// <param name="Crossfade">
/// Whether the list is shown as one picture fading into the next rather than row by row. Every row
/// in <paramref name="Rows"/> is then either leaving or arriving.
/// </param>
/// <param name="Rows">What each row on screen does. A row that only rests is left out.</param>
public sealed record ListMotionPlan<TKey>(bool Crossfade, IReadOnlyList<RowMotion<TKey>> Rows)
{
    /// <summary>Nothing moves.</summary>
    public static ListMotionPlan<TKey> Still { get; } = new(false, []);
}

/// <summary>
/// How a bound list shows a change to it: an arriving row fades and slides in, a leaving row fades
/// out where it stood while the rows below glide up into its space, and a row the sort moved glides
/// to its new place (<see cref="MotionToken.List"/>).
///
/// <para>Decided only for the rows on screen, before and after the change, so a list of thousands
/// costs what its window of rows costs. Every place is a row's top in the list's own viewport, so a
/// scroll the change caused is part of how far a row travelled.</para>
/// </summary>
public static class ListMotion
{
    /// <summary>Closer than this, a row is where it was. Layout rounds to fractions of a pixel.</summary>
    private const double Tolerance = 0.5;

    /// <summary>
    /// What each row on screen does for one change to its list.
    /// </summary>
    /// <param name="before">The top of each row on screen before the change.</param>
    /// <param name="after">Each row on screen after it, and its top, from the top of the list down.</param>
    /// <param name="arrived">The rows the change put into the list.</param>
    /// <param name="departed">The rows the change took out of the list.</param>
    /// <param name="played">How the change plays, from <see cref="MotionToken.List"/>.</param>
    /// <param name="keys">What makes two rows one row, as <paramref name="before"/> is keyed.</param>
    public static ListMotionPlan<TKey> Plan<TKey>(
        IReadOnlyDictionary<TKey, double> before,
        IReadOnlyList<(TKey Key, double Top)> after,
        IReadOnlySet<TKey> arrived,
        IReadOnlySet<TKey> departed,
        Motion played,
        IEqualityComparer<TKey> keys)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(arrived);
        ArgumentNullException.ThrowIfNull(departed);
        ArgumentNullException.ThrowIfNull(keys);

        if (played.IsInstant)
        {
            return ListMotionPlan<TKey>.Still;
        }

        var leaving = before.Where(row => departed.Contains(row.Key)).OrderBy(row => row.Value).ToList();
        var coming = after.Where(row => arrived.Contains(row.Key) && !before.ContainsKey(row.Key)).ToList();

        if (IsBulk(before.Count, after.Count, leaving.Count, coming.Count))
        {
            return Crossfade(before, after, keys);
        }

        var rows = new List<RowMotion<TKey>>(after.Count + leaving.Count);

        rows.AddRange(leaving.Select(row => new RowMotion<TKey>(row.Key, RowMove.Leave, row.Value)));

        var shifts = played.Travels ? Shifts(before, after, arrived) : new double?[after.Count];

        for (var at = 0; at < after.Count; at++)
        {
            var key = after[at].Key;

            if (arrived.Contains(key) && !before.ContainsKey(key))
            {
                rows.Add(new RowMotion<TKey>(key, RowMove.Arrive, 0));
            }
            else if (shifts[at] is { } shift && Math.Abs(shift) > Tolerance)
            {
                rows.Add(new RowMotion<TKey>(key, RowMove.Glide, shift));
            }
        }

        return new ListMotionPlan<TKey>(false, rows);
    }

    /// <summary>
    /// The scroll that keeps the reader's place: how far down the list's view must move so that
    /// the first of <paramref name="anchors"/> that can be held is drawn where it was before the
    /// change. Null where none can be, and the list keeps its own place.
    ///
    /// <para>A row can be held when it was on screen before and after, and it is not the one that
    /// changed: the same staying row is directly above it, and directly below it, as before. A row
    /// the sort moved goes where it now belongs, and the reader sees it go.</para>
    /// </summary>
    /// <param name="anchors">The rows the reader is at, most telling first: under the pointer, then with focus.</param>
    /// <param name="before">The top of each row on screen before the change.</param>
    /// <param name="after">Each row on screen after it, and its top, from the top of the list down.</param>
    /// <param name="keys">What makes two rows one row, as <paramref name="before"/> is keyed.</param>
    public static double? Hold<TKey>(
        IReadOnlyList<TKey> anchors,
        IReadOnlyDictionary<TKey, double> before,
        IReadOnlyList<(TKey Key, double Top)> after,
        IEqualityComparer<TKey> keys)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(keys);

        // The rows on screen both times, in each order: the anchor's neighbours among these are what
        // says whether it was the one that moved.
        var staying = after.Where(row => before.ContainsKey(row.Key)).ToList();
        var was = staying.OrderBy(row => before[row.Key]).Select(row => row.Key).ToList();
        var now = staying.Select(row => row.Key).ToList();

        foreach (var anchor in anchors)
        {
            var at = now.FindIndex(key => keys.Equals(key, anchor));

            if (at < 0)
            {
                continue;
            }

            var held = was.FindIndex(key => keys.Equals(key, anchor));

            if (!SameNeighbours(was, held, now, at, keys))
            {
                continue;
            }

            var shift = staying[at].Top - before[anchor];

            return Math.Abs(shift) > Tolerance ? shift : null;
        }

        return null;
    }

    /// <summary>
    /// The list as one picture fading into the next: every row that is not where it was fades out
    /// where it stood, and every row that is not where it was before fades in where it is now. A row
    /// in the same place both times stays drawn, so what did not change does not flicker.
    /// </summary>
    private static ListMotionPlan<TKey> Crossfade<TKey>(
        IReadOnlyDictionary<TKey, double> before,
        IReadOnlyList<(TKey Key, double Top)> after,
        IEqualityComparer<TKey> keys)
        where TKey : notnull
    {
        var resting = new HashSet<TKey>(
            after.Where(row => before.TryGetValue(row.Key, out var was) && Math.Abs(was - row.Top) <= Tolerance)
                .Select(row => row.Key),
            keys);

        return new ListMotionPlan<TKey>(
            true,
            [
                .. before.Where(row => !resting.Contains(row.Key))
                    .OrderBy(row => row.Value)
                    .Select(row => new RowMotion<TKey>(row.Key, RowMove.Leave, row.Value)),
                .. after.Where(row => !resting.Contains(row.Key))
                    .Select(row => new RowMotion<TKey>(row.Key, RowMove.Arrive, 0)),
            ]);
    }

    /// <summary>
    /// Whether a change is too wide to show row by row: it replaced most of what was on screen, or
    /// filled or emptied the list. A new scan or another folder is then one picture fading into the
    /// next, rather than dozens of rows each sliding.
    /// </summary>
    private static bool IsBulk(int before, int after, int leaving, int coming) =>
        (leaving + coming) * 2 > Math.Max(before, after);

    /// <summary>
    /// How far below its place each row after the change was drawn before it, or null for a row that
    /// does not glide. A row scrolled into the window by the change, that nobody saw before it, goes
    /// with the nearest row above it that was seen, so the rows do not overlap as they glide.
    /// </summary>
    private static double?[] Shifts<TKey>(
        IReadOnlyDictionary<TKey, double> before,
        IReadOnlyList<(TKey Key, double Top)> after,
        IReadOnlySet<TKey> arrived)
        where TKey : notnull
    {
        var shifts = new double?[after.Count];
        double? carried = null;

        for (var at = 0; at < after.Count; at++)
        {
            var (key, top) = after[at];

            if (before.TryGetValue(key, out var was))
            {
                carried = shifts[at] = was - top;
            }
            else if (!arrived.Contains(key))
            {
                shifts[at] = carried;
            }
        }

        // A row above every seen one, scrolled in from the top, goes with the first seen row below.
        carried = null;

        for (var at = after.Count - 1; at >= 0; at--)
        {
            var (key, _) = after[at];

            if (before.ContainsKey(key))
            {
                carried = shifts[at];
            }
            else if (!arrived.Contains(key))
            {
                shifts[at] ??= carried;
            }
        }

        return shifts;
    }

    private static bool SameNeighbours<TKey>(
        List<TKey> was,
        int held,
        List<TKey> now,
        int at,
        IEqualityComparer<TKey> keys)
    {
        return Same(Neighbour(was, held - 1), Neighbour(now, at - 1))
            && Same(Neighbour(was, held + 1), Neighbour(now, at + 1));

        bool Same((bool Has, TKey Key) one, (bool Has, TKey Key) other) =>
            one.Has == other.Has && (!one.Has || keys.Equals(one.Key, other.Key));

        static (bool, TKey) Neighbour(List<TKey> rows, int at) =>
            at >= 0 && at < rows.Count ? (true, rows[at]) : (false, default!);
    }
}
