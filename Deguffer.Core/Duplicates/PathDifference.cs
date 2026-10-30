using System.Buffers;

namespace Deguffer.Core.Duplicates;

/// <summary>A path cut where it differs from the other paths of its group, for the page to show where.</summary>
/// <param name="Same">The start, which another path of the group starts with too.</param>
/// <param name="Differs">What no other path of the group shares at its start or at its end.</param>
/// <param name="SameEnd">The end, which another path of the group ends with too.</param>
public readonly record struct PathParts(string Same, string Differs, string SameEnd)
{
    public override string ToString() => Same + Differs + SameEnd;
}

/// <summary>
/// Where each path of a group differs from the others (§7.4): two near-identical folder names are how
/// a user marks the copy they meant to keep, and a difference of one letter is easy to miss in a
/// column of long paths.
///
/// <para><b>What differs</b> is what is left of a path once the longest start it shares with any
/// other path of the group, and the longest end it shares with any other, are taken away. So
/// <c>Photos 2023</c> and <c>Photos 2O23</c> differ by the one letter however unlike them a third copy
/// is. Compared ordinally, case and all, because a case-sensitive folder can hold <c>Photos</c> and
/// <c>photos</c>, and those differ.</para>
///
/// <para><b>By the letter only where the difference is small.</b> The letters are what a reader needs
/// where two names are near twins, and only there. <c>Documents\Shared album</c> and
/// <c>Downloads\Phone backup</c> share <c>Do</c>, and a difference cut after it starts in the middle
/// of a name and reads as a fault. So a difference keeps its letters only where it lies inside one
/// name and that name shares more characters with its twin than it differs by. Any other difference
/// is widened to the whole names it touches.</para>
///
/// <para>Found by sorting rather than by comparing every pair: the longest start a path shares with
/// any other is the one it shares with a neighbour in ordinal order, and the longest end likewise in
/// the order of the reversed paths, so a group of thousands of copies costs a sort, not millions of
/// comparisons.</para>
/// </summary>
public static class PathDifference
{
    private static readonly SearchValues<char> Separators = SearchValues.Create(@"\/");

    /// <summary>Each of <paramref name="paths"/>, in the same order, cut where it differs from the rest.</summary>
    public static IReadOnlyList<PathParts> Of(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var starts = SharedAtEnds(paths, reversed: false);
        var ends = SharedAtEnds(paths, reversed: true);
        var parts = new PathParts[paths.Count];

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var start = WholeCharacters(path, starts[i], fromStart: true);
            var end = path.Length - WholeCharacters(path, Math.Min(ends[i], path.Length - start), fromStart: false);
            (start, end) = ByLetterOrByName(path, start, end);

            parts[i] = new PathParts(path[..start], path[start..end], path[end..]);
        }

        return parts;
    }

    /// <summary>
    /// The difference between <paramref name="start"/> and <paramref name="end"/> as it is, where it
    /// is small enough to read by the letter, or widened to the whole names it touches.
    /// </summary>
    private static (int Start, int End) ByLetterOrByName(string path, int start, int end)
    {
        if (start == end)
        {
            return (start, end);
        }

        var nameStart = path.AsSpan(0, start).LastIndexOfAny(Separators) + 1;
        var after = path.AsSpan(end).IndexOfAny(Separators);
        var nameEnd = after < 0 ? path.Length : end + after;

        // A difference ending in a separator, or starting with one, ends or starts at a name's edge
        // already, so the names it touches start after it and end before it.
        if (path[end - 1] is '\\' or '/')
        {
            nameEnd = end;
        }

        if (path[start] is '\\' or '/')
        {
            nameStart = start;
        }

        var differs = end - start;
        var withinOneName = path.AsSpan(start, differs).IndexOfAny(Separators) < 0;

        return withinOneName && differs < nameEnd - nameStart - differs ? (start, end) : (nameStart, nameEnd);
    }

    /// <summary>
    /// For each path, the most characters it shares with another path at its start, or at its end
    /// where <paramref name="reversed"/>, found between neighbours in the sorted order.
    /// </summary>
    private static int[] SharedAtEnds(IReadOnlyList<string> paths, bool reversed)
    {
        var keys = paths.Select(path => reversed ? Reverse(path) : path).ToArray();
        var order = Enumerable.Range(0, keys.Length).ToArray();
        Array.Sort(order, (one, other) => string.CompareOrdinal(keys[one], keys[other]));

        var shared = new int[keys.Length];

        for (var i = 1; i < order.Length; i++)
        {
            var common = keys[order[i - 1]].AsSpan().CommonPrefixLength(keys[order[i]]);
            shared[order[i - 1]] = Math.Max(shared[order[i - 1]], common);
            shared[order[i]] = Math.Max(shared[order[i]], common);
        }

        return shared;
    }

    private static string Reverse(string path)
    {
        var characters = path.ToCharArray();
        Array.Reverse(characters);

        return new string(characters);
    }

    /// <summary>
    /// <paramref name="count"/> characters from the start or the end of <paramref name="path"/>, one
    /// fewer where that would cut a character written as two halves, which a name can hold.
    /// </summary>
    private static int WholeCharacters(string path, int count, bool fromStart)
    {
        if (count <= 0 || count >= path.Length)
        {
            return Math.Max(count, 0);
        }

        var cut = fromStart ? count : path.Length - count;

        return char.IsLowSurrogate(path[cut]) && char.IsHighSurrogate(path[cut - 1]) ? count - 1 : count;
    }
}
