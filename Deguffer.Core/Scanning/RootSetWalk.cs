using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>One root of a <see cref="RootSetWalk"/>, and the entries its measurement leaves out.</summary>
/// <param name="Path">
/// The directory, in the form <see cref="Of"/> builds, which is the one the walk spells everything
/// below it in.
/// </param>
internal readonly record struct WalkRoot(string Path, LeftOutEntries? LeftOut)
{
    /// <summary>
    /// <paramref name="path"/> as a root of the walk: extended (§6.3) and without a trailing separator,
    /// so it is spelled exactly as the walk spells the same directory when it meets it below another
    /// root, and the entries <paramref name="bounds"/> leaves out are keyed on that spelling too.
    /// </summary>
    public static WalkRoot Of(string path, ScanBounds bounds)
    {
        var walked = System.IO.Path.TrimEndingDirectorySeparator(LongPath.Extended(path));

        return new WalkRoot(walked, LeftOutEntries.Of(walked, bounds));
    }
}

/// <summary>
/// Several directories measured in one bounded walk, each root keeping a figure of its own.
///
/// <para><b>Why one walk.</b> A provider with many targets used to measure them one after another, each
/// in a walk of its own. A small tree cannot fill the walk's threads, so each walk took about its depth
/// times one listing, and the times added up across hundreds or thousands of targets. Seeded with every
/// root, one walk keeps every thread busy for as long as any root has a directory left to read.</para>
///
/// <para><b>What a directory carries down is which roots it lies in</b>, and for each, the caller's
/// value for its parent there. That is the only per-root state a walk needs, and carrying it is what
/// keeps a root's figure apart from every other's without a lookup a directory.</para>
///
/// <para><b>A root inside another is measured once.</b> It is not seeded. Where the outer walk meets it,
/// it joins the roots that directory lies in, and the files below it count towards both. Where the outer
/// walk never meets it, because a link, a refusal or an entry the outer root leaves out stopped the walk
/// above it, it is walked on its own afterwards, as it would have been alone. A root reached through a
/// link is then followed exactly as a measurement of that path alone follows it.</para>
///
/// <para><b>Paths are compared exactly as spelled, case included.</b> A folder can be made case-sensitive,
/// and there <c>Foo</c> and <c>foo</c> are two folders, so a match that ignored case would add both
/// trees to a root that names one. A root spelled differently from how the outer walk lists it, in case
/// or through an 8.3 alias, is then not recognised below it, and is walked on its own as well, which
/// costs a second reading and changes no figure.</para>
/// </summary>
internal static class RootSetWalk
{
    /// <summary>
    /// Visit every directory at or below each of <paramref name="roots"/>, once for each root it lies in.
    ///
    /// <para><paramref name="onDirectory"/> is handed the root's index, the value it returned for the
    /// directory's parent in that root, which is null for the root itself, the directory's contents and
    /// the names that root leaves out of them. What it returns is handed to each child. It is called
    /// concurrently, and is never asked to descend: every directory entry a root does not leave out is
    /// entered for it, and a link never is.</para>
    /// </summary>
    /// <param name="roots">Directories, each in the form <see cref="WalkRoot.Of"/> builds.</param>
    /// <param name="onProgress">
    /// Called as <see cref="BoundedFileWalk"/>'s progress is, and once more at the end of each further
    /// walk a nested root needs.
    /// </param>
    public static void Visit<TFolder>(
        IReadOnlyList<WalkRoot> roots,
        WalkTuning tuning,
        Func<int, TFolder?, DirectoryContents, IReadOnlySet<string>?, TFolder> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct)
        where TFolder : class =>
        Visit(roots, tuning, WalkListing.FromDisk, onDirectory, onProgress, clock, ct);

    /// <summary>
    /// The walk above, listing each directory through <paramref name="list"/>: the seam a test lists a
    /// tree no volume holds through, and counts what is listed and how much at once.
    /// </summary>
    public static void Visit<TFolder>(
        IReadOnlyList<WalkRoot> roots,
        WalkTuning tuning,
        ListDirectory list,
        Func<int, TFolder?, DirectoryContents, IReadOnlySet<string>?, TFolder> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct)
        where TFolder : class
    {
        IReadOnlyList<int> waiting = [.. Enumerable.Range(0, roots.Count)];

        while (waiting.Count > 0)
        {
            var round = Round<TFolder>.Of(roots, waiting);

            BoundedFileWalk.Visit(
                round.Seeds,
                tuning,
                list,
                (Frame<TFolder>[] frames, DirectoryContents contents, Action<WalkEntry, Frame<TFolder>[]> descend) =>
                    Read(roots, round, frames, contents, descend, onDirectory),
                _ => onProgress(),
                clock,
                ct);

            // Every round reaches at least its seeds, so this ends.
            waiting = [.. waiting.Where(root => !round.WasReached(root))];
        }
    }

    /// <summary>One directory, for every root it lies in, and the roots each of its children lies in.</summary>
    private static void Read<TFolder>(
        IReadOnlyList<WalkRoot> roots,
        Round<TFolder> round,
        Frame<TFolder>[] frames,
        DirectoryContents contents,
        Action<WalkEntry, Frame<TFolder>[]> descend,
        Func<int, TFolder?, DirectoryContents, IReadOnlySet<string>?, TFolder> onDirectory)
        where TFolder : class
    {
        var opened = new (int Root, TFolder Folder, IReadOnlySet<string>? Left)[frames.Length];

        for (var i = 0; i < frames.Length; i++)
        {
            var (root, holder) = frames[i];

            // Asked once a listing, and of each entry only in a listing that holds one.
            var left = roots[root].LeftOut?.NamesIn(contents);
            opened[i] = (root, onDirectory(root, holder, contents, left), left);
        }

        foreach (var entry in contents.Entries)
        {
            // A link is in Links, never here, and the walk refuses to enter one either way.
            if (!entry.IsDirectory)
            {
                continue;
            }

            var continuing = 0;

            foreach (var (_, _, left) in opened)
            {
                if (left?.Contains(entry.Name) != true)
                {
                    continuing++;
                }
            }

            var starting = round.StartingAt(entry);

            if (continuing + starting.Count == 0)
            {
                continue;
            }

            var below = new Frame<TFolder>[continuing + starting.Count];
            var next = 0;

            foreach (var (root, folder, left) in opened)
            {
                if (left?.Contains(entry.Name) != true)
                {
                    below[next++] = new Frame<TFolder>(root, folder);
                }
            }

            foreach (var root in starting)
            {
                below[next++] = new Frame<TFolder>(root, null);
            }

            descend(entry, below);
        }
    }

    /// <summary>One root a directory lies in, and the caller's value for the directory's parent there.</summary>
    private readonly record struct Frame<TFolder>(int Root, TFolder? Holder)
        where TFolder : class;

    /// <summary>
    /// One walk of the roots still waiting: the outermost of them seeded, and the rest found where the
    /// walk meets them.
    /// </summary>
    private sealed class Round<TFolder>
        where TFolder : class
    {
        private static readonly IReadOnlyList<int> None = [];

        /// <summary>The roots that may be met inside another this round, by the path the walk spells them with.</summary>
        private readonly Dictionary<string, int[]>? _nested;

        /// <summary>
        /// Set once for each root the walk starts at, whichever thread meets it. An int each, because
        /// Interlocked has no bool overload.
        /// </summary>
        private readonly int[] _reached;

        private Round(IReadOnlyList<(string, Frame<TFolder>[])> seeds, Dictionary<string, int[]>? nested, int[] reached)
        {
            Seeds = seeds;
            _nested = nested;
            _reached = reached;
        }

        public IReadOnlyList<(string Root, Frame<TFolder>[] State)> Seeds { get; }

        public bool WasReached(int root) => Volatile.Read(ref _reached[root]) == 1;

        /// <summary>The roots that start at <paramref name="entry"/>, which this marks reached.</summary>
        public IReadOnlyList<int> StartingAt(in WalkEntry entry)
        {
            // The common case, a list with nothing nested, builds no path to ask about.
            if (_nested is null || !_nested.TryGetValue(entry.FullName, out var roots))
            {
                return None;
            }

            foreach (var root in roots)
            {
                Interlocked.Exchange(ref _reached[root], 1);
            }

            return roots;
        }

        /// <summary>
        /// Each path asked about once, with every root that asks about it. Ordered with a separator on
        /// the end, a folder's descendants follow it directly, so a path is nested exactly where it
        /// starts with the last one seeded.
        /// </summary>
        public static Round<TFolder> Of(IReadOnlyList<WalkRoot> roots, IReadOnlyList<int> waiting)
        {
            var places = waiting
                .GroupBy(root => roots[root].Path, StringComparer.Ordinal)
                .Select(same => (Path: same.Key, Below: Below(same.Key), Roots: same.ToArray()))
                .OrderBy(place => place.Below, StringComparer.Ordinal);

            var reached = new int[roots.Count];
            var seeds = new List<(string, Frame<TFolder>[])>();
            Dictionary<string, int[]>? nested = null;
            string? outer = null;

            foreach (var (path, below, asking) in places)
            {
                if (outer is not null && below.StartsWith(outer, StringComparison.Ordinal))
                {
                    (nested ??= new Dictionary<string, int[]>(StringComparer.Ordinal))[path] = asking;
                    continue;
                }

                outer = below;
                seeds.Add((path, [.. asking.Select(root => new Frame<TFolder>(root, null))]));

                foreach (var root in asking)
                {
                    reached[root] = 1;
                }
            }

            return new Round<TFolder>(seeds, nested, reached);
        }

        /// <summary>The prefix every path below <paramref name="path"/> starts with. A drive's root already ends in one.</summary>
        private static string Below(string path) =>
            path.EndsWith(System.IO.Path.DirectorySeparatorChar) ? path : path + System.IO.Path.DirectorySeparatorChar;
    }
}
