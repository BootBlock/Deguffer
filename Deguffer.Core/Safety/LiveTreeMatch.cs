using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Deguffer.Core.Safety;

/// <summary>
/// Whether a place a program is makes a directory live, asked at every path each is reachable at, for
/// one reading of the process table. The rule <see cref="LiveTreeInspector"/> applies, and the one its
/// test fake applies, so the two cannot come to disagree.
///
/// <para><b>A program reports a path the way it was started.</b> One working at <c>S:\app</c>, with
/// <c>S:</c> substituted for <c>C:\Source</c>, is working in <c>C:\Source\app</c>, and one running from
/// <c>D:\SysMount\Source\app\bin</c> runs from inside the same project's build output where the system
/// volume is also mounted there. Compared as text, each read as nowhere near the project, so its build
/// output was offered, or a scratch entry it was using was emptied. <see cref="ReachedFolder"/> follows
/// both sides to where they are.</para>
///
/// <para><b>Each path is followed once.</b> Following one asks the machine where its volume is
/// mounted, and one program is many processes started with the same paths (G4). The answers are kept
/// for the life of this instance, which is one reading of the process table.</para>
///
/// <para><b>Each program and each candidate is reached once, not once per pair.</b> A row asks about
/// hundreds of directories against hundreds of processes, so a lookup or an allocation per pair is
/// paid hundreds of thousands of times. <see cref="Programs"/> and <see cref="Candidate"/> follow every
/// place first, and <see cref="AddHolders"/> and <see cref="AddWriter"/> only compare them.</para>
/// </summary>
internal sealed class LiveTreeMatch(IVolumeInventory volumes)
{
    private readonly ConcurrentDictionary<string, ReachedFolder> _reached = new(StringComparer.OrdinalIgnoreCase);
    private ProgramsReached? _programs;

    /// <summary>
    /// The programs in <paramref name="table"/>, each followed once and kept until this instance is
    /// discarded with the reading, so a candidate is compared with places already reached.
    ///
    /// <para><b>Kept for the table it was asked about, by reference.</b> The inspector discards this
    /// instance with its reading, so a second table is never asked about. Were one asked about, it is
    /// followed afresh rather than answered with another reading's programs, which would miss a program
    /// started since in the direction that deletes.</para>
    /// </summary>
    public ImmutableArray<ReachedProgram> Programs(ProcessTable table)
    {
        if (Volatile.Read(ref _programs) is { } held && ReferenceEquals(held.Table, table))
        {
            return held.Programs;
        }

        var reached = new ProgramsReached(
            table,
            [.. table.Processes.Select(process => Program(process.Name, process.ImagePath, process.CurrentDirectory, process.Started))]);

        Volatile.Write(ref _programs, reached);

        return reached.Programs;
    }

    /// <summary>
    /// A program called <paramref name="name"/>, running from <paramref name="image"/> since
    /// <paramref name="started"/> and working in <paramref name="working"/>, with both places followed.
    /// </summary>
    public ReachedProgram Program(string name, string? image, string? working, DateTimeOffset? started) =>
        new(name, image is null ? null : Reach(image), working is null ? null : Reach(working), started);

    /// <summary><paramref name="query"/> with every folder it names followed.</summary>
    public ReachedCandidate Candidate(LiveTreeQuery query) =>
        new(
            Reach(query.Directory),
            Reach(query.Project),
            Path.GetFileName(query.Project),
            [.. query.Workspaces.Select(workspace => (Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace)), Reach(workspace)))],
            query.Writers is { } writers ? [.. writers.Folders.Select(Reach)] : null,
            query.Writers?.LastWritten);

    /// <summary>
    /// Adds to <paramref name="holders"/> what makes <paramref name="candidate"/> live of
    /// <paramref name="program"/>: the executable inside the directory, the working directory anywhere
    /// in the project, or the working directory exactly one of its workspaces.
    ///
    /// <para>Each sentence is what <see cref="Providers.LiveTreeVeto"/> prints, so it keeps its
    /// wording. Nothing is allocated unless a sentence is added, because this runs for every pair of a
    /// candidate and a program.</para>
    /// </summary>
    public static void AddHolders(ReachedCandidate candidate, ReachedProgram program, List<string> holders)
    {
        if (program.Image is { } image && candidate.Directory.Holds(image))
        {
            Add(holders, $"{program.Name} is running from inside it");
        }

        if (program.Working is not { } place)
        {
            return;
        }

        if (candidate.Project.Holds(place))
        {
            Add(holders, $"{program.Name} is working in {candidate.ProjectName}");
        }

        foreach (var workspace in candidate.Workspaces)
        {
            if (workspace.Folder.IsSameAs(place))
            {
                Add(holders, $"{program.Name} is working in {workspace.Name}");
                return;
            }
        }
    }

    /// <summary>
    /// Adds to <paramref name="holders"/> what makes <paramref name="candidate"/> live of
    /// <paramref name="program"/> through <see cref="LiveTreeQuery.Writers"/>: its executable at or below
    /// one of their folders, and its start no later than the directory's last write.
    ///
    /// <para>False where the program runs from one of those folders and either time could not be read.
    /// Whether it was running then cannot be told, which is not the same answer as "it was not".</para>
    /// </summary>
    public static bool AddWriter(ReachedCandidate candidate, ReachedProgram program, List<string> holders)
    {
        if (candidate.WriterFolders is not { } folders || program.Image is not { } image || !AnyHolds(folders, image))
        {
            return true;
        }

        if (program.Started is not { } start || candidate.LastWritten is not { } written)
        {
            return false;
        }

        if (start <= written + TimestampTolerance)
        {
            Add(holders, $"{program.Name} was already running when it was last written");
        }

        return true;
    }

    /// <summary>
    /// Adds <paramref name="holder"/> where it is not there already. One editor is several processes,
    /// and several of them can answer for the same directory.
    /// </summary>
    public static void Add(List<string> holders, string holder)
    {
        if (!holders.Contains(holder, StringComparer.Ordinal))
        {
            holders.Add(holder);
        }
    }

    private static bool AnyHolds(ImmutableArray<ReachedFolder> folders, ReachedFolder inner)
    {
        foreach (var folder in folders)
        {
            if (folder.Holds(inner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far a directory's timestamp may trail the moment it was written. FAT keeps a write time to
    /// two seconds, so a program started a moment before the directory it made could otherwise read
    /// as started after it. Erring this way holds a directory back, never offers one.
    /// </summary>
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The immediate child of one of <paramref name="directories"/> that <paramref name="inside"/>
    /// is at or below, named below that directory as it was asked, or null where it is below none
    /// of them.
    ///
    /// <para>Null for a path that <em>is</em> one of them, which is a program running from a
    /// scratch folder's top level or sitting in it. There is no child to spare in that case, and
    /// the folder itself is never removed, so the honest answer is that this evidence names
    /// nothing, rather than the whole folder.</para>
    /// </summary>
    public string? ChildHolding(IReadOnlyList<string> directories, string inside)
    {
        var place = Reach(inside);

        foreach (var directory in directories)
        {
            // The first segment below the directory, however deep the path runs. Taken from a
            // relative path rather than by string offset, so that both separators and a trailing one
            // are the framework's problem rather than three off-by-one risks here.
            if (Reach(directory).PathTo(place) is not { } relative || relative == ".")
            {
                continue;
            }

            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

            return Path.Combine(directory, separator < 0 ? relative : relative[..separator]);
        }

        return null;
    }

    /// <summary>The folder at <paramref name="path"/>, followed the first time it is asked about.</summary>
    public ReachedFolder Reach(string path) =>
        _reached.GetOrAdd(path, static (key, inventory) => ReachedFolder.At(key, inventory), volumes);

    /// <summary>The programs of one table, kept together so a reader never pairs one with the other's.</summary>
    private sealed record ProgramsReached(ProcessTable Table, ImmutableArray<ReachedProgram> Programs);
}
