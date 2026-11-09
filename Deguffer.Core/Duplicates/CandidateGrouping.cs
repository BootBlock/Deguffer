using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>A file a walk kept, as Windows described it when it was identified.</summary>
internal sealed record IdentifiedFile(FoundFile Found, FileDescription Description);

/// <summary>A group of identified files, before the names of those with several have been listed.</summary>
/// <param name="Files">Each file of the group once, as every path that reached it.</param>
internal sealed record IdentifiedGroup(long? Length, string? Name, DateTime? Modified, IReadOnlyList<IReadOnlyList<IdentifiedFile>> Files);

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
        IReadOnlyList<FoundFile> found,
        MatchCriteria criteria,
        Func<FoundFile, int?> minuteOf)
    {
        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var byTime = criteria.GroupsByTime();

        var placed = new Placement[found.Count];
        var count = 0;

        for (var index = 0; index < found.Count; index++)
        {
            var file = found[index];
            var minute = 0;

            if (byTime)
            {
                if (minuteOf(file) is not { } known)
                {
                    continue;
                }

                minute = known;
            }

            placed[count++] = new Placement(
                bySize ? file.Tree.LengthOf(file.Node) : 0,
                minute,
                byName ? string.GetHashCode(file.Tree.NameSpanOf(file.Node), StringComparison.OrdinalIgnoreCase) : 0,
                index);
        }

        var order = new PlacementOrder(found, byName);
        var sorted = placed.AsSpan(0, count);
        sorted.Sort(order);

        List<(int First, FoundFile[] Files)> groups = [];

        for (int start = 0, end; start < count; start = end)
        {
            end = start + 1;

            while (end < count && order.CompareGroups(sorted[start], sorted[end]) == 0)
            {
                end++;
            }

            if (end - start > 1)
            {
                var files = new FoundFile[end - start];

                for (var member = start; member < end; member++)
                {
                    files[member - start] = found[sorted[member].Index];
                }

                groups.Add((sorted[start].Index, files));
            }
        }

        // In the order they were found, so the order files are identified in does not follow how
        // their lengths happen to sort.
        groups.Sort((x, y) => x.First.CompareTo(y.First));

        return [.. groups.Select(group => group.Files)];
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
    public static IReadOnlyList<IdentifiedGroup> ByTheFiles(IEnumerable<IdentifiedFile> files, MatchCriteria criteria)
    {
        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var byTime = criteria.GroupsByTime();
        var groups = new Dictionary<GroupKey, List<IdentifiedFile>>(KeyComparer.Instance);

        foreach (var file in files)
        {
            var description = file.Description;

            Add(groups, new GroupKey(
                bySize ? description.Length : 0,
                byName ? file.Found : null,
                byTime ? description.Modified.Ticks : 0), file);
        }

        List<IdentifiedGroup> found = [];

        foreach (var (key, members) in groups)
        {
            var distinct = members.GroupBy(member => member.Description.Identity).ToList();

            if (distinct.Count > 1)
            {
                found.Add(new IdentifiedGroup(
                    bySize ? key.Length : null,
                    key.Named is { } named ? named.Tree.NameOf(named.Node) : null,
                    byTime ? new DateTime(key.Time, DateTimeKind.Utc) : null,
                    [.. distinct.Select(paths => (IReadOnlyList<IdentifiedFile>)[.. paths])]));
            }
        }

        return found;
    }

    /// <summary>
    /// The group a search shows. <paramref name="namesOf"/> is asked about the first path of each file
    /// with several names, the path <see cref="NeedingNames"/> gives.
    /// </summary>
    /// <param name="namesOf">Every name of a file with several, given its description.</param>
    public static CandidateGroup WithNames(IdentifiedGroup group, Func<IdentifiedFile, IReadOnlyList<string>> namesOf) =>
        new(group.Length, group.Name, group.Modified, [.. group.Files.Select(paths => Candidate(paths, namesOf))]);

    /// <summary>
    /// Each file of <paramref name="groups"/> with several names, once however many groups hold it, by
    /// the path its names are listed through.
    /// </summary>
    public static IReadOnlyList<IdentifiedFile> NeedingNames(IEnumerable<IdentifiedGroup> groups) =>
        [.. groups
            .SelectMany(group => group.Files)
            .Select(paths => paths[0])
            .Where(file => file.Description.Names > 1)
            .DistinctBy(file => file.Description.Identity)];

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
            Attributes = description.Attributes,
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

    private static void Add(Dictionary<GroupKey, List<IdentifiedFile>> groups, GroupKey key, IdentifiedFile member)
    {
        if (!groups.TryGetValue(key, out var members))
        {
            groups[key] = members = [];
        }

        members.Add(member);
    }

    /// <summary>
    /// A found file by what the tree says places it, flat so a drive's worth sorts with no object for
    /// each. Its name is held as a hash, so only files that may share a name compare names.
    /// </summary>
    /// <param name="Index">Where the file is in the list found, which breaks a tie to keep that order.</param>
    private readonly record struct Placement(long Length, int Minute, int NameHash, int Index);

    /// <summary>
    /// Puts files of one group next to each other, in the order they were found. Names are compared
    /// only where their hashes are equal, ignoring case as the search does (§7.4), so two names that
    /// share a hash stay apart.
    /// </summary>
    private sealed class PlacementOrder(IReadOnlyList<FoundFile> found, bool byName) : IComparer<Placement>
    {
        public int Compare(Placement x, Placement y)
        {
            var group = CompareGroups(x, y);

            return group != 0 ? group : x.Index.CompareTo(y.Index);
        }

        public int CompareGroups(Placement x, Placement y)
        {
            var order = x.Length.CompareTo(y.Length);

            if (order == 0)
            {
                order = x.Minute.CompareTo(y.Minute);
            }

            if (order == 0)
            {
                order = x.NameHash.CompareTo(y.NameHash);
            }

            return order != 0 || !byName
                ? order
                : NameOf(x).CompareTo(NameOf(y), StringComparison.OrdinalIgnoreCase);
        }

        private ReadOnlySpan<char> NameOf(Placement placement)
        {
            var file = found[placement.Index];

            return file.Tree.NameSpanOf(file.Node);
        }
    }

    /// <summary>
    /// What a group is keyed by: the length and the time, and where the name is compared, the file
    /// whose name it is. The name is read where the tree holds it rather than made into a string for
    /// every identified file, of which a search grouping by name can have hundreds of thousands (#345).
    /// </summary>
    private readonly record struct GroupKey(long Length, FoundFile? Named, long Time)
    {
        public ReadOnlySpan<char> Name => Named is { } file ? file.Tree.NameSpanOf(file.Node) : [];
    }

    /// <summary>The length and the time exactly, and the name ordinally without regard to case (§7.4).</summary>
    private sealed class KeyComparer : IEqualityComparer<GroupKey>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(GroupKey x, GroupKey y) =>
            x.Length == y.Length && x.Time == y.Time && x.Name.Equals(y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(GroupKey key) =>
            HashCode.Combine(key.Length, key.Time, string.GetHashCode(key.Name, StringComparison.OrdinalIgnoreCase));
    }
}
