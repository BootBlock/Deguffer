using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// §5.2 applied down a <see cref="CacheLevel"/> table: classify the children of each level, target
/// the recognised ones, and protect the rest.
///
/// <para>One implementation because it carries three safety facts, and a hand-written copy is where
/// one of them goes missing. A level's own directory is checked for being a link before it is
/// listed, a link among the children is declined rather than followed, and a spared child is
/// <em>asserted</em> to survive rather than merely left out of the plan — that last one because a
/// spared child is a sibling of a targeted one under the same parent, which is exactly when an
/// over-broad rule takes both.</para>
///
/// <para>The table stays each provider's own. What is shared is the walk, not the knowledge: which
/// names may go is a fact about one application, and answering it by reading one table in one file
/// is what <see cref="CacheLevel"/> exists for.</para>
/// </summary>
public static class CacheLevelWalk
{
    public const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    /// <summary>
    /// Walk <paramref name="levels"/> under <paramref name="root"/> and report what each child came
    /// to.
    ///
    /// <para>Results are returned rather than appended to lists the caller passes in. A provider
    /// walks several roots and its plan carries the union, so the accumulation belongs to the
    /// provider — and four <c>List&lt;T&gt;</c> parameters threaded through a safety-critical loop
    /// is the shape in which one of them quietly stops being filled.</para>
    /// </summary>
    public static LevelWalk Under(
        IReadOnlyList<CacheLevel> levels,
        string root,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();

        var spared = 0;
        var emptiedAContainer = false;
        var unreadable = false;

        // A container that is a link is met twice: once as a link child of the root, and once as a
        // level whose own directory turns out to be one. Both times it is the same path and the same
        // sentence. Deduplicating per walk rather than over the finished note list is what keeps two
        // roots that happen to share a folder name from collapsing into one note.
        var reportedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var level in levels)
        {
            ct.ThrowIfCancellationRequested();

            var directory = level.Resolve(root);

            if (!LongPath.DirectoryExists(directory))
            {
                continue;
            }

            // Applied at every level rather than only at the ones reached by name. A root usually
            // arrives from an enumeration that filtered links out, so this answers false for it
            // today — and that is the point. Phase 1's junction defect existed because the safety
            // property was riding on a filter nobody had named, and it held only for as long as
            // every target happened to arrive the same way.
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

        return new LevelWalk(targets, declined, survivors, notes, spared, emptiedAContainer, unreadable);

        void Decline(string path)
        {
            if (!reportedLinks.Add(path))
            {
                return;
            }

            notes.Add(Note(path));
            declined.Add((path, LinkReason));
        }
    }

    /// <summary>
    /// The sentence for a link this walk will not follow. Public because a provider says it about
    /// links it meets outside the walk as well — a link where a whole root was expected — and two
    /// wordings for one refusal would read as two different decisions.
    /// </summary>
    public static PlanNote Note(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");
}

/// <summary>What one root's levels came to, for the plan the caller builds from it.</summary>
/// <param name="Targets">The recognised caches, ready to be measured.</param>
/// <param name="Declined">
/// Links that were named and not followed. They go into the plan's protected paths as well as its
/// notes: a declined link is a spared sibling of a targeted directory, which is the case §5.6's
/// negative exists to cover.
/// </param>
/// <param name="Survivors">Every child classified Tier 4, with the reason the user is shown.</param>
/// <param name="Notes">What the walk has to say: a link it declined, a directory it could not list.</param>
/// <param name="Spared">
/// How many children were spared. Counted here rather than from the length of
/// <paramref name="Survivors"/>, which a caller also fills with the root, its parent and named
/// files — a total including those would tell the user that items were left alone in a folder that
/// may not hold them.
/// </param>
/// <param name="EmptiedAContainer">
/// Whether a target came from inside a level below the root. Such a directory is kept, so without
/// this the user sees it still standing and cannot tell that the cache inside it went.
/// </param>
/// <param name="Unreadable">
/// Whether a level's directory would not be listed. A level is reached by name, and a full path
/// resolves through a directory the account may not list — so the level can exist, pass the presence
/// probe, and then hand back no children at all. Without this a caller treats that as "the cache is
/// empty" and reports as clear a folder nobody read.
/// </param>
public readonly record struct LevelWalk(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<(string Path, string Reason)> Survivors,
    IReadOnlyList<PlanNote> Notes,
    int Spared,
    bool EmptiedAContainer,
    bool Unreadable);
