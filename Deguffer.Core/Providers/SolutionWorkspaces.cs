using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Visual Studio solutions in the places a program is, read once for one reading of the process
/// table, so that an editor open on a solution holds back the <c>obj</c> of every project in it (§5.3).
/// See <see cref="LiveTreeQuery.Workspaces"/>.
///
/// <para><b>Read with the process table, and never kept past it.</b> The preview builds one from its
/// table, and the check at the clean builds another from a fresh one: a solution the user opened, or
/// added a project to, between the two is what that check is for, and a reading kept from the preview
/// would not name the new project.</para>
///
/// <para><b>Only the places a program is</b>, because a folder with no program in it is no evidence
/// whatever its solutions say. <b>Only inside an approved root</b>, the consent every source-tree rule
/// keeps: a program somewhere else is no reason to look inside its folder.</para>
/// </summary>
internal sealed class SolutionWorkspaces
{
    private readonly IReadOnlyList<(string Place, Solutions Solutions)> _places;

    private SolutionWorkspaces(IReadOnlyList<(string Place, Solutions Solutions)> places)
    {
        _places = places;
        NamedProjects = [.. places.SelectMany(p => p.Solutions.Projects).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Every project folder a readable solution in one of the places names, wherever it lies. For
    /// finding candidates, where a solution that could not be read has nothing to name.
    /// </summary>
    public IReadOnlyList<string> NamedProjects { get; }

    /// <summary>
    /// The solutions in each place <paramref name="liveTrees"/> finds a program that lies inside one
    /// of <paramref name="roots"/>, at any path either is reachable at, followed through the inspector
    /// so each is followed once in a planning pass.
    ///
    /// <para><b>Each place is named below the root as the root names itself.</b> A program working at
    /// <c>S:\app</c>, with <c>S:</c> substituted for the root <c>C:\Source</c>, is working in
    /// <c>C:\Source\app</c>. The plan names a project below the root, and a solution's projects are
    /// named below the place it was read from, so read through <c>S:</c> they named no project the
    /// plan asks about.</para>
    /// </summary>
    public static SolutionWorkspaces Read(
        ILiveTreeInspector liveTrees,
        IReadOnlyList<SourceRoot> roots,
        CancellationToken ct)
    {
        var places = new Dictionary<string, Solutions>(StringComparer.OrdinalIgnoreCase);
        var folders = roots.Select(root => (root.Path, Folder: liveTrees.Reach(root.Path))).ToList();

        foreach (var place in liveTrees.FindOccupiedDirectories(ct).Live)
        {
            ct.ThrowIfCancellationRequested();

            // Resolved first, because the process table holds whatever form a program was started
            // with: a path with '..' in it would otherwise be followed to somewhere it is not.
            if (LongPath.Configured(place.Directory) is not { } directory)
            {
                continue;
            }

            var reached = liveTrees.Reach(directory);

            foreach (var (root, folder) in folders)
            {
                if (folder.Naming(reached, root) is { } named && !places.ContainsKey(named))
                {
                    places[named] = SolutionsIn(named);
                }
            }
        }

        return new SolutionWorkspaces([.. places.Select(entry => (entry.Key, entry.Value))]);
    }

    /// <summary>
    /// The places whose solutions name <paramref name="project"/>.
    ///
    /// <para>A place holding a solution that could not be read, or that could not be listed at all,
    /// counts for every project below it. It may hold a solution naming any of them, and reading it
    /// as naming none would offer the build output of a project an editor has open. Telling the user
    /// a project looks busy is the direction that costs nothing.</para>
    /// </summary>
    public IReadOnlyList<string> Naming(string project) =>
    [
        .. _places
            .Where(p => p.Solutions.Projects.Contains(project)
                || (p.Solutions.Unreadable && LongPath.Contains(p.Place, project)))
            .Select(p => p.Place),
    ];

    private static Solutions SolutionsIn(string folder)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (FolderEntries.Of(folder) is not { } entries)
        {
            return new Solutions(projects, Unreadable: true);
        }

        var unreadable = false;

        foreach (var entry in entries.OfType<FileInfo>().Where(file => SolutionFile.IsSolution(file.Name)))
        {
            if (SolutionFile.ProjectFolders(Path.Combine(folder, entry.Name)) is { } named)
            {
                projects.UnionWith(named);
            }
            else
            {
                unreadable = true;
            }
        }

        return new Solutions(projects, unreadable);
    }

    /// <param name="Projects">Every project folder a readable solution in the place names.</param>
    /// <param name="Unreadable">Whether the place, or a solution in it, could not be read.</param>
    private sealed record Solutions(IReadOnlySet<string> Projects, bool Unreadable);
}
