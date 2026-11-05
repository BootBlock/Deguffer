using System.Collections.Concurrent;

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
/// for the life of this instance, which is one reading of the process table, and so is where each
/// drive is mounted, which nearly every path shares (<see cref="RememberedMountPoints"/>).
/// <see cref="LiveTreeInspector.DiscardOlderThan"/> drops both with the reading, so where a path is
/// reachable is never older than the table it is compared with.</para>
/// </summary>
internal sealed class LiveTreeMatch(IVolumeInventory volumes)
{
    private readonly ConcurrentDictionary<string, ReachedFolder> _reached = new(StringComparer.OrdinalIgnoreCase);

    private readonly RememberedMountPoints _volumes = new(volumes);

    /// <summary>
    /// What makes <paramref name="candidate"/> live of a program called <paramref name="name"/>
    /// running from <paramref name="image"/> and working in <paramref name="working"/>: the executable
    /// inside the directory, the working directory anywhere in the project, or the working directory
    /// exactly one of its workspaces.
    /// </summary>
    public IEnumerable<string> Holders(LiveTreeQuery candidate, string name, string? image, string? working)
    {
        if (image is not null && Reach(candidate.Directory).Holds(Reach(image)))
        {
            yield return $"{name} is running from inside it";
        }

        if (working is null)
        {
            yield break;
        }

        var place = Reach(working);

        if (Reach(candidate.Project).Holds(place))
        {
            yield return $"{name} is working in {Path.GetFileName(candidate.Project)}";
        }

        if (candidate.Workspaces.FirstOrDefault(workspace => Reach(workspace).IsSameAs(place)) is { } at)
        {
            yield return $"{name} is working in {Path.GetFileName(Path.TrimEndingDirectorySeparator(at))}";
        }
    }

    /// <summary>
    /// What makes <paramref name="candidate"/> live of a program called <paramref name="name"/>
    /// running from <paramref name="image"/> since <paramref name="started"/>, through
    /// <see cref="LiveTreeQuery.Writers"/>: its executable at or below one of their folders, and its
    /// start no later than the directory's last write. Null where it is no such program.
    ///
    /// <para><paramref name="unknown"/> is set where the program runs from one of those folders and
    /// either time could not be read. Whether it was running then cannot be told, which is not the
    /// same answer as "it was not".</para>
    /// </summary>
    public string? Writer(LiveTreeQuery candidate, string name, string? image, DateTimeOffset? started, out bool unknown)
    {
        unknown = false;

        if (candidate.Writers is not { } writers
            || image is null
            || !writers.Folders.Any(folder => Reach(folder).Holds(Reach(image))))
        {
            return null;
        }

        if (started is not { } start || writers.LastWritten is not { } written)
        {
            unknown = true;
            return null;
        }

        return start <= written + TimestampTolerance
            ? $"{name} was already running when it was last written"
            : null;
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
        _reached.GetOrAdd(path, static (key, inventory) => ReachedFolder.At(key, inventory), _volumes);
}
