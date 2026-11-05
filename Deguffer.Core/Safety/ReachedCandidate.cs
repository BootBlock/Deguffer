using System.Collections.Immutable;

namespace Deguffer.Core.Safety;

/// <summary>
/// A directory the in-use check asks about, with every folder its <see cref="LiveTreeQuery"/> names
/// followed to every path it is reachable at once, so that comparing it with each running program
/// looks nothing up.
///
/// <para>Arrays rather than lists, because each is walked once for every program in the table, and a
/// walk over an <see cref="ImmutableArray{T}"/> allocates nothing where one through an interface
/// allocates an enumerator.</para>
/// </summary>
/// <param name="Directory">The directory asked about.</param>
/// <param name="Project">The project it belongs to.</param>
/// <param name="ProjectName">The project's folder name, as a holder names it.</param>
/// <param name="Workspaces">
/// Each of <see cref="LiveTreeQuery.Workspaces"/>, by its folder name as a holder names it.
/// </param>
/// <param name="WriterFolders">
/// The folders of <see cref="LiveTreeQuery.Writers"/>, or null where the query names no writers.
/// </param>
/// <param name="LastWritten">When the directory was last written, as the writers' rule compares it.</param>
internal sealed record ReachedCandidate(
    ReachedFolder Directory,
    ReachedFolder Project,
    string ProjectName,
    ImmutableArray<(string Name, ReachedFolder Folder)> Workspaces,
    ImmutableArray<ReachedFolder>? WriterFolders,
    DateTimeOffset? LastWritten);
