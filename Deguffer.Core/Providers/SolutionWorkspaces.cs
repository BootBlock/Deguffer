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
    /// The solutions in each place of <paramref name="occupied"/> that lies inside one of
    /// <paramref name="roots"/>.
    /// </summary>
    public static SolutionWorkspaces Read(
        IReadOnlyList<LiveTree> occupied,
        IReadOnlyList<SourceRoot> roots,
        CancellationToken ct)
    {
        var places = new Dictionary<string, Solutions>(StringComparer.OrdinalIgnoreCase);

        foreach (var place in occupied)
        {
            ct.ThrowIfCancellationRequested();

            // Resolved first, because the process table holds whatever form a program was started
            // with, and a working directory is read with a trailing separator.
            if (LongPath.Configured(place.Directory) is { } folder
                && roots.Any(root => LongPath.Contains(root.Path, folder))
                && !places.ContainsKey(folder))
            {
                places[folder] = SolutionsIn(folder);
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
