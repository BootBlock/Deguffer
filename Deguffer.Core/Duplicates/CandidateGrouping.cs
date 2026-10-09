namespace Deguffer.Core.Duplicates;

/// <summary>
/// Puts the files a search kept into groups by what can be compared without reading them (§7.4): the
/// length where the size or the content is a criterion, and the name, ordinally and ignoring case,
/// where the name is. A file alone in its group has no match and is dropped.
///
/// <para>The modified time is not compared here. The tree keeps it to the minute, and §7.4 compares
/// times to the file system's full precision, so it waits for the times read from each file
/// (<c>docs/todo/duplicates.md</c>, phase 2). Where it is the only criterion, every file kept is one
/// group until then.</para>
/// </summary>
internal static class CandidateGrouping
{
    public static IReadOnlyList<CandidateGroup> Group(IReadOnlyList<FoundFile> found, MatchCriteria criteria)
    {
        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var groups = new Dictionary<(long Length, string Name), List<FoundFile>>(KeyComparer.Instance);

        foreach (var file in found)
        {
            var key = (bySize ? file.Tree.LengthOf(file.Node) : 0, byName ? file.Tree.NameOf(file.Node) : string.Empty);

            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
            }

            members.Add(file);
        }

        return
        [
            .. groups
                .Where(group => group.Value.Count > 1)
                .Select(group => new CandidateGroup(
                    bySize ? group.Key.Length : null,
                    byName ? group.Key.Name : null,
                    [.. group.Value.Select(Candidate)])),
        ];
    }

    private static DuplicateCandidate Candidate(FoundFile file) => new(
        file.Tree.PathOf(file.Node),
        file.Tree.NameOf(file.Node),
        file.Tree.LengthOf(file.Node),
        file.Tree.StorageOf(file.Node),
        file.Role);

    /// <summary>The length exactly, and the name ordinally without regard to case (§7.4).</summary>
    private sealed class KeyComparer : IEqualityComparer<(long Length, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((long Length, string Name) x, (long Length, string Name) y) =>
            x.Length == y.Length && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((long Length, string Name) key) =>
            HashCode.Combine(key.Length, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
