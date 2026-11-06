namespace Deguffer.Core.Duplicates;

/// <summary>
/// One confirmed group as a keeping rule judges it (§7.4): the space a removal there could free,
/// why no copy can be kept where none can, and what each copy's row lists beside it.
///
/// <para><b>Each copy is asked once.</b> The space, the group's note and every copy's standing are
/// worked out from the same two answers per copy, because each answer asks the policy and walks
/// every protected place for every name the copy has.</para>
///
/// <para><b>Made where the search confirms the group</b> (<see cref="DuplicateMarks.Judge"/>), off
/// the page's thread, so placing the group (<see cref="DuplicateMarks.Add"/>) and drawing its rows
/// ask the rule nothing.</para>
/// </summary>
public sealed class GroupJudgement
{
    internal const string NothingKept =
        "No copy in this group is one Deguffer can count on keeping, so none is marked. Each copy says why.";

    private GroupJudgement(
        DuplicateGroup group, CopyKeeping keeping, long freeableSpace, string? whyNothingCanBeKept, IReadOnlyList<CopyStanding> standings)
    {
        Group = group;
        Keeping = keeping;
        FreeableSpace = freeableSpace;
        WhyNothingCanBeKept = whyNothingCanBeKept;
        Standings = standings;
    }

    public DuplicateGroup Group { get; }

    /// <summary>The keeping rule the group was judged by, which the marks it is added to check is theirs.</summary>
    public CopyKeeping Keeping { get; }

    /// <summary>
    /// The space a removal here could free, which sorts the groups: what every copy that may be
    /// marked occupies, with one copy that can be kept left, which a removal may free less than
    /// (<see cref="DuplicateCandidate.SizeOnDisk"/>). A reference copy, a refused copy and a file with
    /// several names count for nothing, and a group that can keep no copy frees nothing.
    /// </summary>
    public long FreeableSpace { get; }

    /// <summary>Why no copy in the group can be kept, so every copy stays unmarked, or null where one can be.</summary>
    public string? WhyNothingCanBeKept { get; }

    /// <summary>What each copy's row lists beside it, in the order of <see cref="DuplicateGroup.Files"/>.</summary>
    public IReadOnlyList<CopyStanding> Standings { get; }

    /// <summary>Judge <paramref name="group"/> by <paramref name="keeping"/>, asking it once about each copy.</summary>
    public static GroupJudgement Of(DuplicateGroup group, CopyKeeping keeping)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(keeping);

        var files = group.Files;
        var standings = new CopyStanding[files.Count];
        long markable = 0;
        long? smallestKept = null;
        var keptUnmarkable = false;

        for (var i = 0; i < files.Count; i++)
        {
            var copy = files[i];
            var notMarked = keeping.WhyNotMarked(copy);
            var notKept = keeping.WhyNotKept(copy);
            standings[i] = CopyStanding.Of(notMarked, notKept);

            if (notMarked is null)
            {
                markable += copy.SizeOnDisk;
            }

            if (notKept is null)
            {
                smallestKept = Math.Min(smallestKept ?? long.MaxValue, copy.SizeOnDisk);
                keptUnmarkable |= notMarked is not null;
            }
        }

        // Where every copy that can be kept could also be marked, one of them stays: the smallest,
        // so the figure is the most this group could free.
        var freeable = smallestKept switch
        {
            null => 0,
            _ when keptUnmarkable => markable,
            { } smallest => markable - smallest,
        };

        return new GroupJudgement(group, keeping, freeable, smallestKept is null ? NothingKept : null, standings);
    }
}
