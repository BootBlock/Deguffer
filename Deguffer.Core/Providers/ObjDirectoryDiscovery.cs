using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>Candidate directories, and whether the volume index answered for every root.</summary>
/// <param name="Candidates">Every directory of the sought name inside an approved root.</param>
/// <param name="UsedIndex">
/// False if any root had to be walked. §5.5 requires the fallback to be observable, and a discovery
/// pass that took thirty seconds is otherwise indistinguishable from a large source tree.
/// </param>
public sealed record ObjDiscovery(IReadOnlyList<string> Candidates, bool UsedIndex);

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
public sealed class ObjDirectoryDiscovery(IDirectoryScanner scanner)
{
    public async Task<ObjDiscovery> FindAsync(
        string name,
        IReadOnlyList<string> roots,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roots);

        var candidates = new List<string>();
        var usedIndex = true;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            if (!LongPath.DirectoryExists(root))
            {
                // An approved root on a drive that is not currently attached. Finding nothing is
                // the right answer; it is not an error and not a reason to drop the approval.
                continue;
            }

            var indexed = await scanner.TryFindDirectoriesNamedAsync(name, root, ct).ConfigureAwait(false);

            if (indexed is null)
            {
                usedIndex = false;
                Walk(name, root, candidates, ct);
            }
            else
            {
                // The index answers with every directory of that name on the volume, narrowed to
                // this root. Narrowing is not the whole of the walk's behaviour, and the difference
                // is not cosmetic: without this an elevated run offers directories inside .git and
                // node_modules, and nested ones already covered by their own parent.
                candidates.AddRange(
                    indexed.Where(path => SourceTreeBoundary.WouldBeFoundByWalking(path, root, name)));
            }
        }

        // Approved roots may nest or repeat, and the same directory reached twice would become two
        // steps deleting one path.
        return new ObjDiscovery(
            [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)],
            usedIndex);
    }

    /// <summary>
    /// The guaranteed route: enumerate the root ourselves. Iterative rather than recursive, because
    /// the trees this runs over are exactly the deeply nested ones that overflow a stack.
    /// </summary>
    private static void Walk(string name, string root, List<string> candidates, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(LongPath.Extended(root));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var child in ChildDirectories.Under(directory).Directories)
            {
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
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
