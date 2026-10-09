using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>A file a walk kept, as Windows described it when it was identified.</summary>
internal sealed record IdentifiedFile(FoundFile Found, FileDescription Description);

/// <summary>
/// Puts files into groups by what a search compares before it reads their content (§7.4): the length
/// where the size or the content is a criterion, the name, ordinally and ignoring case, where the name
/// is, and the last-modified time where it is. A file alone in its group has no match and is dropped.
///
/// <para><b>Twice: by the tree, then by the files.</b> The tree keeps a time to the minute, so it
/// groups first by what it holds, and only the files sharing a group are identified; they are then
/// grouped again by what Windows says now, the time to the tick. A time equal to the tick is equal to
/// the minute, because both routes truncate the same time to the minute, so the first grouping never
/// parts two files the second would join.</para>
///
/// <para><b>One file is one file.</b> Paths with one identity are one file in a group, which is
/// what keeps a file reached by two paths, or by two of its names, from being its own copy.</para>
/// </summary>
internal static class CandidateGrouping
{
    /// <summary>
    /// The files that share a group by what the tree holds, each group with at least two, before any
    /// file is opened. A file is placed by its minute where <paramref name="minuteOf"/> answers one,
    /// and the caller decides what a file the tree holds no time for is placed by.
    /// </summary>
    /// <param name="minuteOf">The minute a file is placed by, or null to leave it out.</param>
    public static IReadOnlyList<IReadOnlyList<FoundFile>> ByTheTree(
        IEnumerable<FoundFile> found,
        MatchCriteria criteria,
        Func<FoundFile, int?> minuteOf)
    {
        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var byTime = criteria.GroupsByTime();
        var groups = new Dictionary<(long Length, string Name, long Time), List<FoundFile>>(KeyComparer.Instance);

        foreach (var file in found)
        {
            long time = 0;

            if (byTime)
            {
                if (minuteOf(file) is not { } minute)
                {
                    continue;
                }

                time = minute;
            }

            Add(groups, (bySize ? file.Tree.LengthOf(file.Node) : 0, byName ? file.Tree.NameOf(file.Node) : string.Empty, time), file);
        }

        return [.. groups.Values.Where(group => group.Count > 1)];
    }

    /// <summary>The minute the tree holds for a file, or null where it holds none.</summary>
    public static int? TreeMinuteOf(FoundFile file) =>
        file.Tree.ModifiedOf(file.Node) is { IsKnown: true } time ? time.MinutesSinceWindowsEpoch : null;

    /// <summary>The minute a file's full-precision time falls in, as the tree would hold it.</summary>
    public static int MinuteOf(DateTime modified) => ExploreTimestamp.FromUtc(modified).MinutesSinceWindowsEpoch;

    /// <summary>
    /// The groups of identified files by what Windows said of each, every file once a group,
    /// whatever paths reached it.
    /// </summary>
    /// <param name="namesOf">Every name of a file with several, given its description.</param>
    public static IReadOnlyList<CandidateGroup> ByTheFiles(
        IEnumerable<IdentifiedFile> files,
        MatchCriteria criteria,
        Func<IdentifiedFile, IReadOnlyList<string>> namesOf)
    {
        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var byTime = criteria.GroupsByTime();
        var groups = new Dictionary<(long Length, string Name, long Time), List<IdentifiedFile>>(KeyComparer.Instance);

        foreach (var file in files)
        {
            var description = file.Description;

            Add(groups, (
                bySize ? description.Length : 0,
                byName ? file.Found.Tree.NameOf(file.Found.Node) : string.Empty,
                byTime ? description.Modified.Ticks : 0), file);
        }

        List<CandidateGroup> found = [];

        foreach (var (key, members) in groups)
        {
            var distinct = members.GroupBy(member => member.Description.Identity).ToList();

            if (distinct.Count > 1)
            {
                found.Add(new CandidateGroup(
                    bySize ? key.Length : null,
                    byName ? key.Name : null,
                    byTime ? new DateTime(key.Time, DateTimeKind.Utc) : null,
                    [.. distinct.Select(paths => Candidate([.. paths], namesOf))]));
            }
        }

        return found;
    }

    /// <summary>
    /// One file from every path that reached it. A reference where any path is in a reference
    /// location, because a file reached once as a reference must never be offered for removal.
    /// </summary>
    private static DuplicateCandidate Candidate(IReadOnlyList<IdentifiedFile> paths, Func<IdentifiedFile, IReadOnlyList<string>> namesOf)
    {
        var first = paths[0];
        var description = first.Description;
        var tree = first.Found.Tree;

        return new DuplicateCandidate(
            description.Identity,
            description.Path,
            tree.NameOf(first.Found.Node),
            description.Names > 1 ? namesOf(first) : [description.Path],
            description.Names,
            description.Length,
            description.Allocated,
            description.Modified,
            StorageOf(first),
            paths.Any(path => path.Found.Role == LocationRole.Reference) ? LocationRole.Reference : LocationRole.Search)
        {
            Route = first.Found.Route,
            Volume = first.Found.Volume,
        };
    }

    /// <summary>
    /// Whether a file is in the cloud as Windows said when it was identified, which is later than the
    /// scan and can differ either way; anything else as the scan saw it, because only the file table
    /// sees a file Windows itself compressed (<see cref="StorageAttributes"/>).
    /// </summary>
    private static FileStorage StorageOf(IdentifiedFile file)
    {
        var now = StorageAttributes.Of(file.Description.Attributes);
        var scanned = file.Found.Tree.StorageOf(file.Found.Node);

        return now is FileStorage.CloudOnly || scanned is FileStorage.CloudOnly ? now : scanned;
    }

    private static void Add<T>(Dictionary<(long Length, string Name, long Time), List<T>> groups, (long Length, string Name, long Time) key, T member)
    {
        if (!groups.TryGetValue(key, out var members))
        {
            groups[key] = members = [];
        }

        members.Add(member);
    }

    /// <summary>The length and the time exactly, and the name ordinally without regard to case (§7.4).</summary>
    private sealed class KeyComparer : IEqualityComparer<(long Length, string Name, long Time)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((long Length, string Name, long Time) x, (long Length, string Name, long Time) y) =>
            x.Length == y.Length && x.Time == y.Time && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((long Length, string Name, long Time) key) =>
            HashCode.Combine(key.Length, key.Time, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
