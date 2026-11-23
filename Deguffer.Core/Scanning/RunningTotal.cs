using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The running figure of one measurement of several paths: each path's latest reading, added up and
/// reported each time the sum rises. §5.5: stream partial results, never block on a complete scan.
///
/// <para><b>One slot a path.</b> A path can be read by one route and then another: a walk raced
/// against the volume index, or a walk after the table declined the path. Each reading of a path
/// replaces the last one in its slot, so a path two routes read is never counted twice.</para>
///
/// <para><b>A path inside another path asked about is left out of the sum.</b> Its files count towards
/// the outer path as well, so adding both would count them twice. Paths are compared ignoring case,
/// which can only leave out a path that was not nested, so the sum errs low.</para>
///
/// <para><b>A lower bound, never the figure.</b> A reading taken mid-walk falls short of the finished
/// one, so the sum goes out through progress alone. It never falls: one route's answer can come in
/// under what another route had counted of the same path, and a running figure that went down would
/// read as bytes uncounted.</para>
/// </summary>
internal sealed class RunningTotal
{
    private readonly IProgress<ScanSize> _progress;
    private readonly bool[] _counts;
    private readonly ScanSize[] _readings;
    private readonly Lock _gate = new();
    private CountedSum _sum;
    private long _shown = -1;

    private RunningTotal(IReadOnlyList<ScanRequest> asked, IProgress<ScanSize> progress)
    {
        _progress = progress;
        _counts = Outermost(asked);
        _readings = new ScanSize[asked.Count];
    }

    /// <summary>
    /// The running figure of a measurement of <paramref name="asked"/>, reported to
    /// <paramref name="progress"/>, or one that reports nothing where nobody listens.
    /// </summary>
    public static RunningPaths Over(IReadOnlyList<ScanRequest> asked, IProgress<ScanSize>? progress) =>
        progress is null ? RunningPaths.None : new RunningPaths(new RunningTotal(asked, progress), null);

    /// <summary>
    /// Put the reading <paramref name="readingOf"/> gives for each of the first <paramref name="count"/>
    /// of <paramref name="indexes"/> in the slot of that path, then report the sum once if it rose.
    /// </summary>
    /// <param name="places">
    /// The slot of each path of the part reporting, or null where the part is the whole measurement.
    /// </param>
    /// <param name="indexes">
    /// The paths among the part's that have a reading, or null where they are its first
    /// <paramref name="count"/>.
    /// </param>
    /// <param name="readingOf">The reading of a path, given its index among the part's.</param>
    internal void Report(int[]? places, IReadOnlyList<int>? indexes, int count, Func<int, ScanSize> readingOf)
    {
        // One at a time, so the sum is read whole and the caller's handler never runs beside itself.
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var index = indexes?[i] ?? i;
                var slot = places?[index] ?? index;
                var reading = readingOf(index);

                // Kept as it goes rather than added up again, so a measurement of N paths reported one
                // at a time costs N steps rather than N squared.
                if (_counts[slot])
                {
                    _sum.Replace(_readings[slot], reading);
                }

                _readings[slot] = reading;
            }

            var sum = _sum.Total;

            if (sum.Reclaimable > _shown)
            {
                _shown = sum.Reclaimable;
                _progress.Report(sum);
            }
        }
    }

    /// <summary>
    /// True for each path that lies inside no other path asked about, and for the first of any path
    /// asked about twice.
    ///
    /// <para>Sorted rather than compared in pairs, because a provider can ask about thousands of paths.
    /// Every path below another starts with that path and its separator, so it sorts into one run
    /// straight after it, and one pass over the sorted keys finds them all, as
    /// <see cref="RootSetWalk"/> finds the roots it does not seed. Unlike the walk, which has to match a
    /// folder exactly as it lists it, this ignores case, so it can only err low.</para>
    /// </summary>
    private static bool[] Outermost(IReadOnlyList<ScanRequest> asked)
    {
        var keys = new string[asked.Count];

        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = LongPath.Below(Path.TrimEndingDirectorySeparator(LongPath.Extended(asked[i].Path))).ToUpperInvariant();
        }

        var counts = new bool[keys.Length];
        string? outer = null;

        // A stable sort, so the first of two equal paths is the one counted.
        foreach (var i in Enumerable.Range(0, keys.Length).OrderBy(i => keys[i], StringComparer.Ordinal))
        {
            if (outer is null || !keys[i].StartsWith(outer, StringComparison.Ordinal))
            {
                counts[i] = true;
                outer = keys[i];
            }
        }

        return counts;
    }

    /// <summary>
    /// The sum of the counted slots, as parts a reading can be taken back out of exactly.
    ///
    /// <para>Not a <see cref="ScanSize"/>, because its subtraction clamps at zero and cannot take a
    /// flag back out: a total stays approximate once any part was, even after that part is replaced
    /// by an exact reading. So each flag is a count of the slots that carry it.</para>
    /// </summary>
    private struct CountedSum
    {
        private long _allocated;
        private long _logical;
        private long _entries;
        private int _approximate;
        private int _ceilings;

        public readonly ScanSize Total => new(_allocated, _logical, _approximate > 0, _entries, _ceilings > 0);

        public void Replace(ScanSize old, ScanSize reading)
        {
            _allocated += reading.Allocated - old.Allocated;
            _logical += reading.Logical - old.Logical;
            _entries += reading.Entries - old.Entries;
            _approximate += Flag(reading.IsApproximate) - Flag(old.IsApproximate);
            _ceilings += Flag(reading.IsCeiling) - Flag(old.IsCeiling);
        }

        private static int Flag(bool set) => set ? 1 : 0;
    }
}

/// <summary>
/// Some of the paths of a <see cref="RunningTotal"/>, as a part of the measurement sees them: the
/// files a scanner reads one by one, a walk of the paths on one kind of drive, or the paths one
/// route is left to answer. A reading reported here goes in the slot of the path it describes.
/// </summary>
internal readonly struct RunningPaths
{
    private readonly RunningTotal? _total;

    /// <summary>The place in the whole measurement of each path here, or null where these are all of them.</summary>
    private readonly int[]? _places;

    internal RunningPaths(RunningTotal? total, int[]? places)
    {
        _total = total;
        _places = places;
    }

    /// <summary>A measurement nobody listens to, which reports nothing.</summary>
    public static RunningPaths None => default;

    /// <summary>The paths at <paramref name="indexes"/> among these, in that order.</summary>
    public RunningPaths Of(IReadOnlyList<int> indexes)
    {
        if (_total is null)
        {
            return None;
        }

        var places = new int[indexes.Count];

        for (var i = 0; i < places.Length; i++)
        {
            places[i] = _places?[indexes[i]] ?? indexes[i];
        }

        return new RunningPaths(_total, places);
    }

    /// <summary>
    /// A final reading of each of the paths at <paramref name="indexes"/> among these, given its index
    /// by <paramref name="readingOf"/>, and one report for all of them: the files a probe read whole,
    /// or the paths one look at a table answered.
    /// </summary>
    public void Report(IReadOnlyList<int> indexes, Func<int, ScanSize> readingOf)
    {
        // No path answered is nothing to say. The first sum always goes on, so a report of none would
        // send a zero ahead of every real figure.
        if (indexes.Count > 0)
        {
            _total?.Report(_places, indexes, indexes.Count, readingOf);
        }
    }

    /// <summary>
    /// A reading of each of the first <paramref name="count"/> paths here, from one look at a walk, and
    /// one report for all of them.
    /// </summary>
    public void Report(int count, Func<int, ScanSize> readingOf) =>
        _total?.Report(_places, null, count, readingOf);
}
