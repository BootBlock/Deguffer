namespace Deguffer.Core.Memory;

/// <summary>
/// Decides whether the undocumented memory lists can be believed, by the one sum Windows documents:
/// available memory is the standby, free and zeroed pages together.
/// </summary>
internal static class MemoryListCheck
{
    /// <summary>
    /// The drift allowed between the two reads: a fiftieth of physical memory, and never less than
    /// 64 MiB. They are separate calls, and a machine can move that much in between. Measured on one
    /// machine, the two agreed to within a thousandth of a percent; a list read at the wrong offset is
    /// off by whole lists, not by a fiftieth.
    /// </summary>
    private const long MarginFloor = 64L * 1024 * 1024;

    /// <summary>
    /// The lists as a snapshot carries them: none where Windows did not return the whole structure,
    /// none where they do not add up to <paramref name="available"/>, and the lists themselves only
    /// where they do.
    /// </summary>
    /// <param name="returned">Whether Windows returned the structure, at the size it has.</param>
    /// <param name="counts">The page counts, which are not read at all unless <paramref name="returned"/> is true.</param>
    public static (MemoryLists? Lists, MemoryListState State) Accept(
        bool returned, ReadOnlySpan<nuint> counts, long pageSize, long available, long physicalTotal)
    {
        if (!returned)
        {
            return (null, MemoryListState.NotReturned);
        }

        var lists = MemoryLists.FromPageCounts(counts, pageSize);

        return Agrees(lists, available, physicalTotal)
            ? (lists, MemoryListState.Checked)
            : (null, MemoryListState.Disagrees);
    }

    public static bool Agrees(MemoryLists lists, long available, long physicalTotal) =>
        Math.Abs(lists.Zeroed + lists.Free + lists.Standby - available)
            <= Math.Max(MarginFloor, physicalTotal / 50);
}
