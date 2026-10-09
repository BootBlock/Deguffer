namespace Deguffer.Core.Duplicates;

/// <summary>
/// A named rule that marks copies across every group (§7.4): the one place Deguffer acts on more than
/// the user picked out by hand, so it marks and never removes, and every mark it makes is listed
/// before anything goes and can be undone.
///
/// <para><b>Every rule obeys the keeping rule.</b> A rule that keeps a copy chooses it only from the
/// unmarked copies that can be kept, so "keep the newest" keeps the newest of those, and a newer copy
/// on a USB drive is marked. A rule that can keep nothing in a group marks nothing there. No rule
/// marks a copy in a cloud folder, which goes from every device that syncs it, and every mark goes
/// through <see cref="GroupMarks.Mark"/>, which refuses whatever would leave a group no copy to keep.
/// A rule adds to the marks there are.</para>
/// </summary>
public abstract record MarkingRule
{
    private MarkingRule()
    {
    }

    /// <summary>Keep the copy last modified most recently, and mark the rest.</summary>
    public sealed record KeepNewest : MarkingRule;

    /// <summary>Keep the copy last modified longest ago, and mark the rest.</summary>
    public sealed record KeepOldest : MarkingRule;

    /// <summary>Keep the copy with the shortest path, and mark the rest.</summary>
    public sealed record KeepShortestPath : MarkingRule;

    /// <summary>Keep every copy in <paramref name="Folder"/>, and mark the rest, in a group with a copy there that can be kept.</summary>
    public sealed record KeepInFolder(string Folder) : MarkingRule;

    /// <summary>Mark every copy in <paramref name="Folder"/>, in a group that still keeps a copy elsewhere.</summary>
    public sealed record MarkInFolder(string Folder) : MarkingRule;
}
