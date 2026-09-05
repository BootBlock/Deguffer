using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What one root's levels came to, for the sentences a plan carries.</summary>
/// <param name="Spared">
/// How many children were classified Tier 4 and left alone. Counted here rather than from the
/// length of the survivor list, which also carries the roots and any named files — a total
/// including those would tell the user that items were left alone in a folder that may not hold
/// them.
/// </param>
/// <param name="EmptiedAContainer">
/// Whether a target came from inside a level with a container of its own. Such a directory is kept,
/// so without this the user sees it still standing and cannot tell that the cache inside it went.
/// </param>
/// <param name="Unreadable">
/// Whether a level's directory would not be listed. A level is reached by name, and a full path
/// resolves through a directory the account may not list — so the level can exist, pass a presence
/// probe, and then hand back no children at all. Without this a provider treats that as "the cache
/// is empty" and contradicts its own probe within one planning pass.
/// </param>
internal readonly record struct LevelWalkOutcome(int Spared, bool EmptiedAContainer, bool Unreadable);

/// <summary>
/// §5.2 applied to a set of <see cref="CacheLevel"/>s under one root: classify the children of each
/// level, target the recognised ones, and assert the rest survived.
///
/// <para>One implementation because a spared child is a sibling of a targeted one under the same
/// parent, which is exactly when an over-broad rule takes both — so it is asserted to survive
/// rather than merely left out of the plan, and that has to be true of every provider that works
/// this way rather than of whichever one remembered it. It carries three further rules a
/// hand-written copy loses one of: a level whose own directory is a link is declined rather than
/// looked through, a link child is named rather than dropped silently, and a level that refuses to
/// be listed says so rather than reading as empty.</para>
/// </summary>
internal static class CacheLevelWalk
{
    internal const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    internal static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");

    /// <summary>
    /// Walk every level under <paramref name="root"/>, adding to the four collections the caller
    /// will build its plan from.
    /// </summary>
    /// <param name="root">The directory each level resolves against.</param>
    /// <param name="levels">
    /// The levels, in the order their notes should read. A level whose directory is absent is
    /// skipped silently: absence is a complete answer, and nothing inside it can be reached.
    /// </param>
    public static LevelWalkOutcome Collect(
        string root,
        IReadOnlyList<CacheLevel> levels,
        ICollection<DeletionTarget> targets,
        ICollection<(string Path, string Reason)> declined,
        ICollection<(string Path, string Reason)> survivors,
        ICollection<PlanNote> notes,
        CancellationToken ct)
    {
        var spared = 0;
        var emptiedAContainer = false;
        var unreadable = false;

        // A container that is a link is met twice: once as a link child of its parent level, and
        // once as a level whose own directory turns out to be one. Both times it is the same path
        // and the same sentence. Deduplicating here rather than over the finished note list is what
        // keeps two roots that happen to share a folder name from collapsing into one note.
        var reportedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var level in levels)
        {
            ct.ThrowIfCancellationRequested();

            var directory = level.Resolve(root);

            if (!LongPath.DirectoryExists(directory))
            {
                continue;
            }

            // Applied at every level rather than only at the ones reached by name. A level whose
            // directory arrived from an enumeration that filtered links out answers false here
            // today — and that is the point. A safety property riding on a filter nobody has named
            // holds only for as long as every target happens to arrive the same way.
            if (LongPath.IsReparsePoint(directory))
            {
                Decline(directory);
                continue;
            }

            var scan = ChildDirectories.Under(directory);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(directory));
                unreadable = true;
                continue;
            }

            foreach (var link in scan.Links)
            {
                Decline(LongPath.Display(link.FullName));
            }

            foreach (var child in scan.Directories)
            {
                var classification = level.Children.Classify(child.Name);
                var path = LongPath.Display(child.FullName);

                if (classification.Tier.IsOfferable())
                {
                    targets.Add(new DeletionTarget(path, classification.Reason));
                    emptiedAContainer |= level.ContainerName.Length > 0;
                }
                else
                {
                    survivors.Add((path, classification.Reason));
                    spared++;
                }
            }
        }

        return new LevelWalkOutcome(spared, emptiedAContainer, unreadable);

        void Decline(string path)
        {
            if (!reportedLinks.Add(path))
            {
                return;
            }

            notes.Add(LinkNote(path));
            declined.Add((path, LinkReason));
        }
    }
}
