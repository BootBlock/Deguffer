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
/// <param name="Notes">What the user is told, including anything left alone and why.</param>
public sealed record DeclaredLocationScan(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Protected,
    IReadOnlyList<string> Declined,
    IReadOnlyList<PlanNote> Notes)
{
    /// <summary>Whether this machine gave the provider anything at all to say.</summary>
    public bool FoundNothing => Targets.Count == 0 && Declined.Count == 0;
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

    public static DeclaredLocationScan Examine(IReadOnlyList<DeclaredRoot> roots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var targets = new List<DeletionTarget>();
        var protectedPaths = new List<(string Path, string Reason)>();
        var declined = new List<string>();
        var notes = new List<PlanNote>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            if (!LongPath.DirectoryExists(root.Path))
            {
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
                Collect(root, location, targets, protectedPaths, declined, notes);
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

        return new DeclaredLocationScan(targets, Deduplicate(protectedPaths), declined, notes);
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
        List<PlanNote> notes)
    {
        var segments = location.RelativePath.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root.Path;
        var ancestors = new List<(string Path, string Reason)>(segments.Length);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);

            if (!LongPath.DirectoryExists(current))
            {
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

        if (!(isFile ? LongPath.FileExists(path) : LongPath.DirectoryExists(path)))
        {
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

        targets.Add(new DeletionTarget(
            path,
            location.Reason,
            LastWritten(path, isFile),
            isFile ? TargetKind.File : TargetKind.Directory,
            root.RequiresElevation));
    }

    /// <summary>
    /// Leave a path alone because it is a link, and say so three ways: named to the user, asserted
    /// to have survived, and counted so the provider can tell this from an empty machine.
    /// </summary>
    private static void Decline(
        string path,
        List<string> declined,
        List<(string Path, string Reason)> protectedPaths,
        List<PlanNote> notes)
    {
        declined.Add(path);
        protectedPaths.Add((path, LinkReason));
        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving '{LongPath.Display(path)}' alone: it is a link to somewhere else, and Deguffer "
            + "does not look through a link."));
    }

    /// <summary>
    /// §7's age. For a file its own timestamp, which for <c>MEMORY.DMP</c> is the moment the machine
    /// stopped. For a directory the newest of its own timestamp and its immediate children's.
    ///
    /// The Recycle Bin could use a directory's own timestamp because nothing in a bin is ever
    /// rewritten in place. These are logs, and a log is appended to — which moves the file and
    /// leaves the parent untouched, so the directory alone would report a servicing log being
    /// written right now as months old. One level is enough for every location declared here: the
    /// logs and dumps sit directly inside, and where a report is a directory of its own its arrival
    /// moves the parent anyway.
    /// </summary>
    private static DateTime? LastWritten(string path, bool isFile)
    {
        var extended = LongPath.Extended(path);

        try
        {
            if (isFile)
            {
                return new FileInfo(extended).LastWriteTimeUtc;
            }

            var directory = new DirectoryInfo(extended);
            var newest = directory.LastWriteTimeUtc;

            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                if (child.LastWriteTimeUtc > newest)
                {
                    newest = child.LastWriteTimeUtc;
                }
            }

            return newest;
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
