using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What a set of <see cref="DeclaredRoot"/> declarations turned out to be on this machine.</summary>
/// <param name="Targets">The declared paths that are there, ready to be measured.</param>
/// <param name="Protected">
/// What §5.6 asserts survived: every root, every §9 exclusion named under one, every directory
/// between a root and a target, and anything declined below.
/// </param>
/// <param name="Declined">
/// Declared paths left alone because they, or something above them, turned out to be a link.
///
/// Reported separately from <paramref name="Protected"/>, which holds them too, because a provider
/// has to tell "there is nothing here" from "there is something here and Deguffer refused it". A
/// plan collapsed to a bare "nothing found" in the second case would drop the note explaining the
/// refusal, and quietly disagree with a folder the user can see.
/// </param>
/// <param name="Unreachable">
/// Declared paths Windows would not describe, so nothing here establishes whether they are there.
///
/// <para>Separate from <paramref name="Declined"/> because the two send the reader to different
/// places: a decline is Deguffer's own, and this is Windows'. Separate from
/// <paramref name="Protected"/> for a harder reason — §5.6 reads a path it cannot measure as
/// "nothing to preserve", so asserting one here would pass whatever happened to the folder.</para>
/// </param>
/// <param name="Notes">What the user is told, including anything left alone and why.</param>
public sealed record DeclaredLocationScan(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Protected,
    IReadOnlyList<string> Declined,
    IReadOnlyList<PlanNote> Notes)
{
    /// <summary>
    /// Declared paths Windows refused to describe. Empty on the ordinary machine, so it is defaulted
    /// rather than made a positional member: every caller that does not yet know about a refusal
    /// keeps working, and reads the empty list as the absence of one.
    /// </summary>
    public IReadOnlyList<string> Unreachable { get; init; } = [];

    /// <summary>
    /// Whether the provider owes the user <see cref="CleanupPlan.HasUnreadableRoot"/>, because part
    /// of what it declares could not be reached at all.
    /// </summary>
    public bool CouldNotBeReached => Unreachable.Count > 0;

    /// <summary>
    /// Whether this machine gave the provider anything at all to say.
    ///
    /// <para>A refusal counts as something to say. It is the answer that used to be folded into
    /// absence, which is how a provider came to report a cache on the disk as a tool that is not
    /// installed. See <see cref="Safety.PathPresence"/>.</para>
    /// </summary>
    public bool FoundNothing => Targets.Count == 0 && Declined.Count == 0 && Unreachable.Count == 0;

    /// <summary>
    /// Whether a plan built from this scan would offer nothing while leaving a declared path
    /// unexamined — the combination <see cref="FoundNothing"/> deliberately excludes.
    ///
    /// <para>A scan with a decline and no target passes the "nothing found" gate, so the provider
    /// builds its ordinary plan and that plan has no steps. See
    /// <see cref="CleanupPlan.WasNotExamined"/> for why a row in that state must not read
    /// "Already clear": a junctioned local repository is present, measures zero, and holds
    /// everything.</para>
    /// </summary>
    public bool NothingWasExamined => Targets.Count == 0 && Declined.Count > 0;
}

/// <summary>
/// Resolves declared paths against the disk, for the providers that name their targets outright
/// rather than finding them.
///
/// <para>Shared by two providers because what it carries is a fact rather than a shape: a target
/// reached by name has none of the protection an enumeration gives away for free, and the GPU
/// shader caches are where that was learned — a safety property was riding on a filter nobody had
/// named, and it held only while every target happened to arrive the same way. Written twice, one
/// copy would eventually lose the reparse check or the ancestor assertions. Each provider still owns
/// its own declaration, so "which paths may this tool delete?" is still answered by reading one
/// table in one file.</para>
/// </summary>
public static class DeclaredLocations
{
    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static DeclaredLocationScan Examine(IReadOnlyList<DeclaredRoot> roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var targets = new List<DeletionTarget>();
        var protectedPaths = new List<(string Path, string Reason)>();
        var declined = new List<string>();
        var unreachable = new List<string>();
        var notes = new List<PlanNote>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            switch (LongPath.ProbeDirectory(root.Path))
            {
                case PathPresence.Refused:
                    Unreachable(root.Path, unreachable, notes);
                    continue;

                case PathPresence.Absent:
                    continue;
            }

            // The root arrives by name, so nothing has classified it. A junctioned root hands back
            // the far side's ordinary directories, which would be targeted while every survivor
            // named for it resolves through the link and passes — the vacuous negative.
            if (LongPath.IsReparsePoint(root.Path))
            {
                Decline(root.Path, declined, protectedPaths, notes);
                continue;
            }

            protectedPaths.Add((root.Path, root.Reason));
            protectedPaths.AddRange(
                root.ProtectedNames.Select(p => (Path.Combine(root.Path, p.RelativePath), p.Reason)));

            foreach (var location in root.Locations)
            {
                ct.ThrowIfCancellationRequested();
                Collect(root, location, targets, protectedPaths, declined, unreachable, notes);
            }
        }

        if (targets.Any(t => t.RequiresElevation))
        {
            // Stated whether or not this process is elevated, because it is a fact about the
            // locations rather than about the run. Showing the sentence only when it applies would
            // need Core to know the process token, and a plan that described the disk differently
            // depending on who asked would be a worse thing to have.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Some of these sit in directories only an administrator may change. Deguffer lists "
                + "them either way, and can remove them only while it is running as administrator."));
        }

        return new DeclaredLocationScan(targets, Deduplicate(protectedPaths), declined, notes)
        {
            Unreachable = unreachable,
        };
    }

    /// <summary>
    /// One declared path: walk down to it, protecting each directory passed through, and target it
    /// if it is there.
    ///
    /// The walk is the point. A nested declaration such as <c>Logs\CBS</c> can be reached through a
    /// junction at <em>any</em> level, and a check on the final path alone would miss a junctioned
    /// <c>Logs</c> — after which the deletion lands in a tree the plan never named.
    /// </summary>
    private static void Collect(
        DeclaredRoot root,
        DeclaredLocation location,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> protectedPaths,
        List<string> declined,
        List<string> unreachable,
        List<PlanNote> notes)
    {
        // Both separators, because Windows accepts both and the cost of missing one is silent. A
        // declaration written as "Logs/CBS" would otherwise split into a single segment, skip the
        // ancestor walk entirely, and still resolve to a valid path through Path.Combine — which is
        // precisely the junctioned-container case the walk exists to catch.
        var segments = location.RelativePath.Split(
            Separators, StringSplitOptions.RemoveEmptyEntries);

        var current = root.Path;
        var ancestors = new List<(string Path, string Reason)>(segments.Length);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);

            switch (LongPath.ProbeDirectory(current))
            {
                case PathPresence.Refused:
                    Unreachable(current, unreachable, notes);
                    return;

                case PathPresence.Absent:
                    return;
            }

            if (LongPath.IsReparsePoint(current))
            {
                Decline(current, declined, protectedPaths, notes);
                return;
            }

            ancestors.Add((
                current,
                $"The {segments[i]} directory itself must survive — only what is named inside it is removed."));
        }

        var path = Path.Combine(root.Path, location.RelativePath);
        var isFile = location.Kind == DeclaredLocationKind.File;

        switch (isFile ? LongPath.ProbeFile(path) : LongPath.ProbeDirectory(path))
        {
            case PathPresence.Refused:
                Unreachable(path, unreachable, notes);
                return;

            case PathPresence.Absent:
                return;
        }

        if (LongPath.IsReparsePoint(path))
        {
            Decline(path, declined, protectedPaths, notes);
            return;
        }

        // Committed only now, so the §5.6 report names the containers something was actually taken
        // out of rather than every directory the declaration happens to mention.
        protectedPaths.AddRange(ancestors);

        if (location.Kind == DeclaredLocationKind.DirectoryContents)
        {
            // Both a target and a survivor, which is the whole of what this kind means. The
            // assertion is worth making rather than assuming: an ordinary removal takes the
            // directory with its contents, so a bound that failed to hold would leave the folder
            // gone and look exactly like a deletion that worked.
            protectedPaths.Add((
                path,
                $"The {Path.GetFileName(path)} folder itself must survive — only what is inside it "
                + "is removed."));
        }

        targets.Add(new DeletionTarget(
            path,
            location.Reason,
            location.ReportsAge ? LastWritten(path, isFile) : null,
            location.Kind switch
            {
                DeclaredLocationKind.File => TargetKind.File,
                DeclaredLocationKind.DirectoryContents => TargetKind.DirectoryContents,
                _ => TargetKind.Directory,
            },
            root.RequiresElevation));
    }

    /// <summary>
    /// Leave a path alone because it is a link, and say so three ways: named to the user, asserted
    /// to have survived, and counted so the provider can tell this from an empty machine.
    ///
    /// Once per path, however many declarations run through it. Two locations under one container
    /// each walk the whole chain down from the root, so a junctioned container is met once per
    /// location — and the plan would carry the same sentence twice, which reads as two folders
    /// rather than one.
    /// </summary>
    private static void Decline(
        string path,
        List<string> declined,
        List<(string Path, string Reason)> protectedPaths,
        List<PlanNote> notes)
    {
        if (declined.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        declined.Add(path);
        protectedPaths.Add((path, LinkReason));
        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving '{LongPath.Display(path)}' alone: it is a link to somewhere else, and Deguffer "
            + "does not look through a link."));
    }

    /// <summary>
    /// Record that Windows would not describe a declared path, once however many declarations run
    /// through it — the same rule <see cref="Decline"/> gives, and for the same reason.
    ///
    /// <para><b>No protected path.</b> §5.6 measures a survivor before the run, and a path that
    /// cannot be measured records itself as "nothing to preserve" and then passes over whatever
    /// happened to it. An assertion nobody can fail is worse than none, because it reads as one
    /// that held.</para>
    /// </summary>
    private static void Unreachable(string path, List<string> unreachable, List<PlanNote> notes)
    {
        if (unreachable.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        unreachable.Add(path);
        notes.Add(UnreadableRoot.UnreachedNote(LongPath.Display(path)));
    }

    /// <summary>
    /// §7's age. A directory is dated by <see cref="DirectoryAge"/>, the one rule every provider
    /// asking this question uses. A file is dated by its own timestamp, which for <c>MEMORY.DMP</c>
    /// is the moment the machine stopped.
    ///
    /// One level is enough for the locations declared here because their own children are the things
    /// being written: the logs and dumps sit directly inside, and where a report is a directory of
    /// its own, its arrival moves the parent. Where that does not hold, the location says so and is
    /// never asked — see <see cref="DeclaredLocation.ReportsAge"/>.
    /// </summary>
    private static DateTime? LastWritten(string path, bool isFile)
    {
        if (!isFile)
        {
            return DirectoryAge.Of(path);
        }

        try
        {
            return new FileInfo(LongPath.Extended(path)).LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // §5.3 makes a refusal ordinary here, and §7 renders a null as unknown rather than as
            // an age — which is the honest rendering, since an age is what invites a deletion.
            return null;
        }
    }

    /// <summary>
    /// One entry per path, keeping the first reason given.
    ///
    /// Two declarations under one container both protect it, and a root that is also a container
    /// would be named twice — which would report one survivor as two, exactly as the single-profile
    /// Chromium layout did.
    /// </summary>
    private static IReadOnlyList<(string Path, string Reason)> Deduplicate(
        IReadOnlyList<(string Path, string Reason)> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return [.. paths.Where(p => seen.Add(p.Path))];
    }
}
