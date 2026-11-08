using System.Collections.Frozen;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Finds directories by name inside the roots the user approved — the first thing Deguffer looks
/// for that has no fixed location.
///
/// Every other provider knows where to look because a toolchain owns one cache directory. Source
/// trees are wherever the developer keeps them, so discovery is its own concern, and it is bounded
/// by consent rather than by a tool's layout: an approved root is the only place this looks, and a
/// directory found outside one is never returned. The volume index makes that cheap; it does not
/// make it implicit.
///
/// <para><b>Only where the answer can matter.</b> A root that nothing in the scope reaches is not
/// looked in, and is named in <see cref="SourceDiscovery.NotSearchedRoots"/> instead. One a scan
/// listed whole is read from the scan's tree rather than listed again. Storage passes no scope, so
/// every root is searched on the disk.</para>
/// </summary>
public sealed class SourceDirectoryDiscovery
{
    private readonly IDirectoryScanner _scanner;
    private readonly IVolumeInventory _volumes;
    private readonly ScanTuner _tuning;
    private readonly ProtectionScope _scope;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>
    /// The search this pass made, or is making, and the roots it was asked about.
    ///
    /// Every provider sharing this instance asks the same question with the same roots in the same
    /// planning pass, and the answer costs a walk of the developer's whole disk. The providers are
    /// planned together, so the search is kept from the moment it starts: a provider that asks while
    /// it runs waits for it rather than walking the disk beside it. Keyed on the roots rather than
    /// assumed constant: two callers with different roots must never be handed each other's answer,
    /// and that mistake would show up as a plan targeting a folder nobody approved.
    ///
    /// <para>Compared by value, which for a <see cref="SourceRoot"/> is the folder and the answer the
    /// user gave about its volume together. A comparison that ignored the second would hand a caller
    /// that has the approval the answer built for one that has not — an empty result where the plan
    /// should hold every project in the folder, or the reverse.</para>
    /// </summary>
    private (IReadOnlyList<SourceRoot> Roots, Task<SourceDiscovery> Search)? _memo;

    /// <summary>
    /// Whether a scan's tree is too old to read. <see cref="Invalidate"/> asks for the folders as they
    /// are now, which the tree does not say, so every root searched after it is searched on the disk.
    /// </summary>
    private volatile bool _listingStale;

    /// <param name="volumes">
    /// The machine's volumes, so that <see cref="Searches"/> can tell a root on a local disk from one
    /// on a cloud client's mount. The shared inventory by default, whose list a planning pass drops
    /// at its start like every other cached view of the machine.
    /// </param>
    /// <param name="tuning">What the walk runs with. <see cref="ScanTuner.Shipped"/> where none is given.</param>
    /// <param name="scope">What the answer will be asked about. <see cref="ProtectionScope.Everything"/> where none is given.</param>
    public SourceDirectoryDiscovery(
        IDirectoryScanner scanner, IVolumeInventory? volumes = null, ScanTuner? tuning = null, ProtectionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        _scanner = scanner;
        _volumes = volumes ?? VolumeInventory.Current;
        _tuning = tuning ?? ScanTuner.Shipped;
        _scope = scope ?? ProtectionScope.Everything;
    }

    /// <summary>
    /// Add the names one provider searches for. Called by every provider that shares this instance,
    /// which is what makes the set the union of what the whole default provider list looks for
    /// without that union being written down anywhere a provider could drift from.
    ///
    /// <para>The complete set matters for both of the things this type has to get right. One pass
    /// answers for all of them, where six providers each walking every approved source root would
    /// be six full enumerations of the developer's disk on an unelevated run — §5.5's own complaint
    /// restated. And what counts as "inside another candidate" is a property of the set rather than
    /// of one name: a pass that knows about <c>node_modules</c> stops there and never offers the
    /// <c>build</c> directory of a package inside it, where a pass that does not walks straight in.
    /// Every route is given this set, so none applies a rule another does not — which is as far as
    /// they can be held together, and <see cref="SourceTreeBoundary"/> says where that stops.</para>
    /// </summary>
    public void Include(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            throw new ArgumentException("At least one directory name is required.", nameof(names));
        }

        lock (_gate)
        {
            var added = false;

            // A plain loop rather than Any(_names.Add), which stops at the first name that is new
            // and silently drops the rest — so a provider declaring two names would search for one.
            foreach (var name in names)
            {
                added |= _names.Add(name);
            }

            if (added)
            {
                // A name added after a pass would make that pass's answer incomplete, and its
                // "inside another candidate" judgements wrong. Both are corrected by asking again.
                _memo = null;
            }
        }
    }

    /// <summary>Every directory of a sought name inside <paramref name="roots"/>.</summary>
    public Task<SourceDiscovery> FindAsync(IReadOnlyList<SourceRoot> roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        Task<Task<SourceDiscovery>> starting;
        Task<SourceDiscovery> started;

        lock (_gate)
        {
            // A search that failed or was stopped is not an answer, and the next caller asks again.
            if (_memo is { Search: { IsFaulted: false, IsCanceled: false } search } memo && memo.Roots.SequenceEqual(roots))
            {
                return search;
            }

            starting = new Task<Task<SourceDiscovery>>(() => SearchAllAsync(roots, ct));
            started = starting.Unwrap();
            _memo = ([.. roots], started);
        }

        // Started outside the lock, on the caller's thread: a walk
        // under the lock would hold every other caller on a thread rather than on the task.
        starting.RunSynchronously();

        return started;
    }

    private async Task<SourceDiscovery> SearchAllAsync(IReadOnlyList<SourceRoot> roots, CancellationToken ct)
    {
        var search = Search();

        var candidates = new List<string>();
        var unreadable = new List<string>();
        var unreached = new List<string>();
        var refused = new List<string>();
        var notSearched = new List<string>();
        var fellBack = false;

        // Each folder a pass will search, as the user spelled it the first time it appears. Gathered
        // before any search starts, because an outer root can hand a nested one off only if it knows
        // the nested one will be searched.
        var searchable = new List<string>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // First, and ahead of the existence check: this is the gate that stops a download, and a
            // gate behind another check is one an edit to that check can switch off.
            if (!Searches(root))
            {
                refused.Add(root.Path);
                continue;
            }

            switch (LongPath.ProbeDirectory(root.Path))
            {
                // Windows would not say whether the root is there, so no part of a folder the user
                // approved was searched. That is what UnreadableDirectories reports, and it is not
                // the same as a drive that is not attached.
                case PathPresence.Refused:
                    unreached.Add(root.Path);
                    continue;

                // An approved root on a drive that is not currently attached. Finding nothing is
                // the right answer; it is not an error and not a reason to drop the approval.
                case PathPresence.Absent:
                    continue;
            }

            if (!searchable.Exists(other => SameFolder(other, root.Path)))
            {
                searchable.Add(root.Path);
            }
        }

        var found = await SearchEachAsync(search, searchable, ct).ConfigureAwait(false);

        // Joined in the order the folders were approved, whatever order their searches finished in.
        // No two searches cover one directory (a nested folder is handed off), so this is the answer a
        // search of one folder after another gives, down to which spelling the distinct step keeps.
        for (var i = 0; i < searchable.Count; i++)
        {
            if (found[i] is not { } searched)
            {
                notSearched.Add(searchable[i]);
                continue;
            }

            candidates.AddRange(searched.Candidates);
            unreadable.AddRange(searched.Unreadable);
            fellBack |= searched.FellBack;
        }

        // A root repeated in another case is still searched twice, and the same directory reached
        // twice would become two steps deleting one path.
        var result = new SourceDiscovery(
            [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)],
            fellBack,
            [.. unreadable.Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. refused.Distinct(StringComparer.OrdinalIgnoreCase)])
        {
            UnreachedRoots = [.. unreached.Distinct(StringComparer.OrdinalIgnoreCase)],
            NotSearchedRoots = notSearched,
        };

        return result;
    }

    /// <summary>
    /// Search every one of <paramref name="searchable"/>, each lane of them at the same time as the
    /// others (<see cref="SourceSearchLanes"/>), up to <see cref="CleanupPlanner.ProvidersAtOnce"/>
    /// folders at once.
    ///
    /// <para><b>One failure stops the rest.</b> The discovery has failed once any search has, and
    /// the others would otherwise walk whole folders for a result nobody receives. What failed is
    /// what is thrown, never the stop it caused in the others.</para>
    /// </summary>
    /// <returns>Each folder's search in the slot of its folder, or null for one the scope does not reach.</returns>
    private async Task<RootSearch?[]> SearchEachAsync(
        DirectorySearch search, IReadOnlyList<string> searchable, CancellationToken ct)
    {
        var found = new RootSearch?[searchable.Count];

        // Planned on a worker thread, because learning which disks a volume is on opens its devices
        // and waits on them. A single folder has nothing to search beside it.
        IReadOnlyList<IReadOnlyList<int>> lanes = searchable.Count < 2
            ? [[.. Enumerable.Range(0, searchable.Count)]]
            : await WorkerThreads.Shared.RunAsync(() => SourceSearchLanes.Of(searchable, _tuning.StorageOf), ct).ConfigureAwait(false);

        // Parallel.ForEachAsync stops every lane once one throws, and throws the failure ahead of
        // the cancellations it caused, because it records the failure before it stops the others.
        await Parallel.ForEachAsync(
            lanes,
            new ParallelOptions { MaxDegreeOfParallelism = CleanupPlanner.ProvidersAtOnce, CancellationToken = ct },
            async (lane, stop) =>
            {
                foreach (var index in lane)
                {
                    stop.ThrowIfCancellationRequested();
                    found[index] = await SearchOneAsync(search, searchable[index], searchable, stop).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        return found;
    }

    /// <summary>
    /// Search <paramref name="root"/>, stopping at every other of <paramref name="searchable"/> inside
    /// it, or null where the scope does not reach it.
    /// </summary>
    private async Task<RootSearch?> SearchOneAsync(
        DirectorySearch search, string root, IReadOnlyList<string> searchable, CancellationToken ct)
    {
        // Waits for a scan's tree where the scope is a scan's, because only the tree says whether
        // the scan ended with one.
        var reach = await _scope.ReachAsync(root, ct).ConfigureAwait(false);

        if (reach.Reach == FolderReach.Outside)
        {
            return null;
        }

        var rootSearch = search.HandingOff(Nested(root, searchable));

        return reach is { Reach: FolderReach.Listed, Listing: { } tree } && !_listingStale
            ? FromListing(SourceTreeWalk.In(tree, reach.Node, root, rootSearch, ct))
            : await SearchAsync(rootSearch, root, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a pass would look inside <paramref name="root"/> at all.
    ///
    /// <para>False only for a root on a volume whose contents are not on this machine that the user
    /// has not approved for it. Enumerating such a volume downloads the user's files instead of
    /// measuring them, and no per-entry test can tell it from a disk, so the location is what has to
    /// be refused — <see cref="LocalVolume.StoresContentRemotely"/> says how one is recognised and
    /// what it does not recognise. A volume the inventory reports nothing about is searched, as every
    /// volume was before that reading existed: refusing on no reading would be a guess.</para>
    ///
    /// <para>Public for a caller that reached a root some other way and has to apply the same
    /// refusal, on the reasoning <see cref="WithinTheSearch"/> gives — two routes over the same roots
    /// that disagree about which of them they may read are two different rules.</para>
    /// </summary>
    public bool Searches(SourceRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return root.RemoteStorageApproved
            || HostVolume.For(_volumes, root.Path) is not { StoresContentRemotely: true };
    }

    /// <summary>
    /// The <paramref name="candidates"/> a pass over <paramref name="root"/> searches as far as, by
    /// the boundary a pass applies with every name this discovery has been given.
    ///
    /// <para>For a caller that reached a directory some other way, and must not answer for one a
    /// pass would never have offered. The whole name set is asked rather than the caller's own
    /// names, for the reason <see cref="Include"/> gives: a directory below any candidate belongs to
    /// that candidate. Each candidate must already be known to sit below <paramref name="root"/>, as
    /// <see cref="SourceTreeBoundary.IsInsideTheSearch"/> requires. Links are not judged here: a
    /// recogniser refuses one at a candidate or its project, and one higher up is not detected.</para>
    /// </summary>
    public IReadOnlyList<string> WithinTheSearch(IReadOnlyList<string> candidates, string root)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var search = Search();

        return [.. candidates.Where(candidate => SourceTreeBoundary.IsInsideTheSearch(candidate, root, search))];
    }

    /// <summary>
    /// Forget the last pass, so a source root added in Settings is picked up on the next preview.
    /// Reached through each provider's own <c>InvalidateCaches</c>.
    ///
    /// <para>The volume list goes with it. <see cref="Searches"/> reads a snapshot, and a cloud client
    /// that mounted itself while the app was open would be missing from a stale one — which is the
    /// direction that ends in a download rather than in a missed folder. Every provider invalidates
    /// before any of them plans, so the several calls this instance receives cost one read of the
    /// volume table rather than one each.</para>
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _memo = null;
        }

        _listingStale = true;
        _volumes.Invalidate();
    }

    /// <summary>
    /// Each of <paramref name="searchable"/> strictly inside <paramref name="root"/>, as components
    /// below it, for the search of <paramref name="root"/> to stop at.
    ///
    /// <para>Every one of them is searched on its own, so a search that also entered it listed every
    /// directory in it twice, which is up to half of discovery when the nested folder holds most of
    /// the outer one. Stopping at it gives the same answer, because each route judges a directory by
    /// its name and its own descent alone, never by the path above it. A nested root the outer search
    /// could never have reached, behind a link or below a directory it does not enter, loses
    /// nothing.</para>
    ///
    /// <para>Compared as spelled. A nested root spelled in another case from its parent is not found
    /// here, which costs a second listing of it rather than a folder nobody searched.</para>
    ///
    /// <para>Internal for the test that hands its answer to the walk, which is where a spelling
    /// the two disagreed on would show.</para>
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Nested(string root, IReadOnlyList<string> searchable) =>
    [
        .. searchable
            .Where(other => !SameFolder(other, root)
                && LongPath.Contains(Spelled(root), Spelled(other), StringComparison.Ordinal))
            .Select(other => LongPath.Relative(Spelled(root), Spelled(other)).Split(Path.DirectorySeparatorChar)),
    ];

    /// <summary>
    /// Whether two approved roots name one folder as spelled, so the second is not searched again.
    /// </summary>
    private static bool SameFolder(string first, string second) =>
        Spelled(first).Equals(Spelled(second), StringComparison.Ordinal);

    /// <summary>A root in display form, without the separator a picker or a hand may leave on it.</summary>
    private static string Spelled(string root) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(root));

    /// <summary>
    /// The search for every name registered so far. Internal for the benchmark, which walks with the
    /// search a scan uses.
    /// </summary>
    internal DirectorySearch Search()
    {
        FrozenSet<string> names;

        lock (_gate)
        {
            names = _names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                "No directory names have been registered. Every provider sharing a discovery calls " +
                "Include before planning.");
        }

        return SourceTreeBoundary.Searching(names);
    }

    /// <summary>What a search of one root found, and how.</summary>
    /// <param name="RootListed">Whether the walk listed the root itself. Always true of the index.</param>
    private sealed record RootSearch(
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> Unreadable,
        bool RootListed,
        bool FellBack = false);

    /// <summary>
    /// Search <paramref name="root"/> through the volume index, the walk, or both at once, as the
    /// scanner says. Racing, a walk that could not list the root has found nothing the index cannot
    /// find, so the index is waited for.
    /// </summary>
    private async Task<RootSearch> SearchAsync(DirectorySearch search, string root, CancellationToken ct)
    {
        if (!_scanner.RacesTheWalk)
        {
            // Before the question, which can start a build that would otherwise yield to walks this
            // search is not making.
            _scanner.NeedIndex(root);

            return await FromIndexAsync(search, root, ct).ConfigureAwait(false)
                ?? (await WorkerThreads.Shared.RunAsync(() => Walk(search, root, ct), ct).ConfigureAwait(false)) with { FellBack = true };
        }

        var raced = await RouteRace.FirstAsync(
            stop => FromIndexAsync(search, root, stop),
            stop => WorkerThreads.Shared.RunAsync(() => Walk(search, root, stop), stop),
            walked => walked.RootListed,
            () => _scanner.NeedIndex(root),
            ct).ConfigureAwait(false);

        return raced.Outcome is RaceOutcome.TableDeclined ? raced.Answer with { FellBack = true } : raced.Answer;
    }

    /// <summary>
    /// The index's answer for every name in <paramref name="root"/>, or null where it has none.
    ///
    /// <para>One question for the whole set, answered by one pass over the root's own subtree that
    /// stops where the walk stops. What the index cannot share with the walk is its reach, which is
    /// a property of the token: <see cref="SourceTreeBoundary"/> says why, and says why that is
    /// reach rather than licence.</para>
    /// </summary>
    private async Task<RootSearch?> FromIndexAsync(DirectorySearch search, string root, CancellationToken ct) =>
        await _scanner.TryFindDirectoriesAsync(search, root, ct).ConfigureAwait(false) is { } indexed
            ? new RootSearch(indexed, [], RootListed: true)
            : null;

    /// <summary>
    /// The guaranteed route: enumerate the root ourselves, telling the scanner so for the length of the
    /// walk, so a volume index read on the same disk reads after it.
    /// </summary>
    private RootSearch Walk(DirectorySearch search, string root, CancellationToken ct)
    {
        using var walking = _scanner.Walking(root);

        return FromListing(SourceTreeWalk.Under(root, search, _tuning.For(root).Walk, ct));
    }

    private static RootSearch FromListing(WalkedSourceTree walked) =>
        new(walked.Candidates, walked.Unreadable, walked.RootListed);
}
