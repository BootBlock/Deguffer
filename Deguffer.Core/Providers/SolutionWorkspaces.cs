using System.Collections.Concurrent;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which of the places a program is in hold a Visual Studio solution that names a given .NET project,
/// so that an editor open on a solution holds back the <c>obj</c> of every project in it (§5.3). See
/// <see cref="LiveTreeQuery.Workspaces"/>.
///
/// <para><b>Asked of the places a program is, not of every folder above a project.</b> The places
/// are few, and each is read once for the operation. A folder with no program in it is no evidence
/// whatever its solutions say, so reading it would cost a listing and decide nothing.</para>
///
/// <para><b>Only inside an approved root</b>, the consent every source-tree rule keeps: a program
/// somewhere else is no reason to look inside its folder.</para>
/// </summary>
internal sealed class SolutionWorkspaces
{
    private readonly ConcurrentDictionary<string, Solutions> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The places among <paramref name="occupied"/>, inside one of <paramref name="roots"/>, whose
    /// solutions name <paramref name="project"/>.
    ///
    /// <para>A place holding a solution that could not be read counts for every project below it.
    /// That solution may name any of them, and reading it as naming none would offer the build output
    /// of a project an editor has open. Telling the user a project below looks busy is the direction
    /// that costs nothing.</para>
    /// </summary>
    public IReadOnlyList<string> Naming(string project, IReadOnlyList<LiveTree> occupied, IReadOnlyList<SourceRoot> roots)
    {
        var naming = new List<string>();

        foreach (var place in occupied)
        {
            // Resolved first, because the process table holds whatever form a program was started
            // with, and a working directory is read with a trailing separator.
            if (LongPath.Configured(place.Directory) is not { } folder
                || !roots.Any(root => LongPath.Contains(root.Path, folder)))
            {
                continue;
            }

            var solutions = Read(folder);

            if (solutions.Projects.Contains(project) || (solutions.Unreadable && LongPath.Contains(folder, project)))
            {
                naming.Add(folder);
            }
        }

        return naming;
    }

    /// <summary>
    /// The project folders the solutions in <paramref name="folder"/> name, as far as they could be
    /// read. For finding candidates, where a solution that could not be read has nothing to offer.
    /// </summary>
    public IReadOnlyList<string> NamedIn(string folder) => [.. Read(folder).Projects];

    /// <summary>Forget every folder read, so a solution edited since is read again.</summary>
    public void Invalidate() => _read.Clear();

    private Solutions Read(string folder) => _read.GetOrAdd(folder, static folder =>
    {
        if (FolderEntries.Of(folder) is not { } entries)
        {
            // A folder that refused to be listed may hold a solution, and is answered as one that
            // could not be read.
            return new Solutions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), Unreadable: true);
        }

        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    });

    /// <param name="Projects">Every project folder a readable solution in the folder names.</param>
    /// <param name="Unreadable">Whether the folder, or a solution in it, could not be read.</param>
    private sealed record Solutions(IReadOnlySet<string> Projects, bool Unreadable);
}
