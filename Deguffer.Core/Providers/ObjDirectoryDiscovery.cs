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
    /// <summary>
    /// Directories that would slow the walk considerably and can never hold a recognised candidate
    /// beneath them. <c>node_modules</c> is the expensive one — hundreds of thousands of entries in
    /// a tree that has no .NET intermediate output in it.
    /// </summary>
    private static readonly string[] NeverDescended = [".git", "node_modules"];

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
                candidates.AddRange(indexed);
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

            foreach (var child in EnumerateSafely(directory))
            {
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    // Not descended into: anything below a candidate belongs to that candidate, and
                    // an obj nested inside an obj is part of the output already being considered.
                    candidates.Add(LongPath.Display(child.FullName));
                    continue;
                }

                if (!NeverDescended.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child.FullName);
                }
            }
        }
    }

    /// <summary>
    /// §5.3: a directory we cannot read is skipped rather than being a reason to abandon the scan.
    /// Reparse points are never followed — a symlinked source folder must not walk discovery out of
    /// the root the user approved and into a system directory.
    /// </summary>
    private static List<DirectoryInfo> EnumerateSafely(string extendedDirectory)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(extendedDirectory)
                    .EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }
}
