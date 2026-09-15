using System.Collections.Frozen;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>Candidate directories, and whether the volume index answered for every root.</summary>
/// <param name="Candidates">Every directory of a sought name inside an approved root.</param>
/// <param name="UsedIndex">
/// False if any root had to be walked. §5.5 requires the fallback to be observable, and a discovery
/// pass that took thirty seconds is otherwise indistinguishable from a large source tree.
/// </param>
/// <param name="UnreadableDirectories">
/// Directories inside an approved root that refused to be listed, so the walk never went below
/// them. Reported rather than dropped: part of a root the user approved went unsearched, and a plan
/// that says nothing about it describes a search it did not perform. Always empty on an indexed
/// run, which reads the volume table rather than enumerating.
/// </param>
/// <param name="RefusedRoots">
/// Approved roots no pass looked inside at all, because each is on a volume whose contents are not
/// on this machine and the user has not said to search one anyway. Reported for the reason
/// <paramref name="UnreadableDirectories"/> is, and with more force: this is Deguffer's own decision
/// rather than Windows', and the whole of a folder the user chose went unsearched.
/// </param>
public sealed record SourceDiscovery(
    IReadOnlyList<string> Candidates,
    bool UsedIndex,
    IReadOnlyList<string> UnreadableDirectories,
    IReadOnlyList<string> RefusedRoots)
{
    /// <summary>The candidates called any of <paramref name="names"/>, for the provider that owns them.</summary>
    public IReadOnlyList<string> Named(IReadOnlyList<string> names) =>
    [
        .. Candidates.Where(c => names.Contains(
            Path.GetFileName(c.TrimEnd(Path.DirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase)),
    ];
}

/// <summary>
/// Finds directories by name inside the roots the user approved — the first thing Deguffer looks
/// for that has no fixed location.
///
/// Every other provider knows where to look because a toolchain owns one cache directory. Source
/// trees are wherever the developer keeps them, so discovery is its own concern, and it is bounded
/// by consent rather than by a tool's layout: an approved root is the only place this looks, and a
/// directory found outside one is never returned. The volume index makes that cheap; it does not
/// make it implicit.
/// </summary>
public sealed class SourceDirectoryDiscovery
{
    private readonly IDirectoryScanner _scanner;
    private readonly IVolumeInventory _volumes;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private (IReadOnlyList<SourceRoot> Roots, SourceDiscovery Result)? _memo;

    /// <param name="volumes">
    /// The machine's volumes, so that <see cref="Searches"/> can tell a root on a local disk from one
    /// on a cloud client's mount. The shared inventory by default, whose list a planning pass drops
    /// at its start like every other cached view of the machine.
    /// </param>
    public SourceDirectoryDiscovery(IDirectoryScanner scanner, IVolumeInventory? volumes = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        _scanner = scanner;
        _volumes = volumes ?? VolumeInventory.Current;
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
    /// Both routes are given this set, so neither applies a rule the other does not — which is as
    /// far as the two can be held together, and <see cref="SourceTreeBoundary"/> says where that
    /// stops.</para>
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
    public async Task<SourceDiscovery> FindAsync(IReadOnlyList<SourceRoot> roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        if (Remembered(roots) is { } memo)
        {
            return memo;
        }

        var names = Names();

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                "No directory names have been registered. Every provider sharing a discovery calls " +
                "Include before planning.");
        }

        var candidates = new List<string>();
        var unreadable = new List<string>();
        var refused = new List<string>();
        var usedIndex = true;

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
                    unreadable.Add(root.Path);
                    continue;

                // An approved root on a drive that is not currently attached. Finding nothing is
                // the right answer; it is not an error and not a reason to drop the approval.
                case PathPresence.Absent:
                    continue;
            }

            var walked = false;

            foreach (var name in names)
            {
                var indexed = await _scanner.TryFindDirectoriesNamedAsync(name, root.Path, ct).ConfigureAwait(false);

                if (indexed is null)
                {
                    // One walk answers for every name at once, so the remaining names need no pass
                    // of their own.
                    usedIndex = false;
                    walked = true;
                    break;
                }

                // The index answers with every directory of that name on the volume, narrowed to
                // this root. Narrowing is not the whole of the boundary, and the difference is not
                // cosmetic: without this an elevated run offers directories inside .git and
                // node_modules, and nested ones already covered by their own parent. What the
                // filter cannot restore is the walk's reach, which is a property of the token —
                // SourceTreeBoundary says why, and says why that is reach rather than licence.
                candidates.AddRange(
                    indexed.Where(path => SourceTreeBoundary.IsInsideTheSearch(path, root.Path, names)));
            }

            if (walked)
            {
                Walk(names, root.Path, candidates, unreadable, ct);
            }
        }

        // Approved roots may nest or repeat, and the same directory reached twice would become two
        // steps deleting one path.
        var result = new SourceDiscovery(
            [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)],
            usedIndex,
            [.. unreadable.Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. refused.Distinct(StringComparer.OrdinalIgnoreCase)]);

        Remember(roots, result);

        return result;
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

        var names = Names();

        return [.. candidates.Where(candidate => SourceTreeBoundary.IsInsideTheSearch(candidate, root, names))];
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

        _volumes.Invalidate();
    }

    /// <summary>
    /// The result of the pass this one repeats, if it does.
    ///
    /// Every provider sharing this instance asks the same question with the same roots in the same
    /// planning pass, and the answer costs a walk of the developer's whole disk. Keyed on the roots
    /// rather than assumed constant: two callers with different roots must never be handed each
    /// other's answer, and that mistake would show up as a plan targeting a folder nobody approved.
    ///
    /// <para>Compared by value, which for a <see cref="SourceRoot"/> is the folder and the answer the
    /// user gave about its volume together. A comparison that ignored the second would hand a caller
    /// that has the approval the answer built for one that has not — an empty result where the plan
    /// should hold every project in the folder, or the reverse.</para>
    /// </summary>
    private SourceDiscovery? Remembered(IReadOnlyList<SourceRoot> roots)
    {
        lock (_gate)
        {
            return _memo is { } memo && memo.Roots.SequenceEqual(roots)
                ? memo.Result
                : null;
        }
    }

    private FrozenSet<string> Names()
    {
        lock (_gate)
        {
            return _names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Remember(IReadOnlyList<SourceRoot> roots, SourceDiscovery result)
    {
        lock (_gate)
        {
            _memo = ([.. roots], result);
        }
    }

    /// <summary>
    /// The guaranteed route: enumerate the root ourselves. Iterative rather than recursive, because
    /// the trees this runs over are exactly the deeply nested ones that overflow a stack.
    /// </summary>
    private static void Walk(
        FrozenSet<string> names,
        string root,
        List<string> candidates,
        List<string> unreadable,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(LongPath.Extended(root));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            var scan = ChildDirectories.Under(directory);

            if (scan.Unreadable)
            {
                // Everything below here went unsearched. Silence would leave the plan describing a
                // sweep of the whole approved root, which is not what happened.
                unreadable.Add(LongPath.Display(directory));
                continue;
            }

            foreach (var child in scan.Directories)
            {
                if (names.Contains(child.Name))
                {
                    // Not descended into: anything below a candidate belongs to that candidate, and
                    // an obj nested inside an obj is part of the output already being considered.
                    candidates.Add(LongPath.Display(child.FullName));
                    continue;
                }

                if (!SourceTreeBoundary.IsNeverEntered(child.Name))
                {
                    pending.Push(child.FullName);
                }
            }
        }
    }
}
