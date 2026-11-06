using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The marks on one group's copies, which say what a removal would take (§7.4).
///
/// <para><b>Every group keeps a copy, by construction.</b> A mark is refused where the copy is never
/// marked or is refused, and where it would leave the group with no unmarked copy that can be kept.
/// The rule is asked of the <see cref="CopyKeeping"/> handed in at each question rather than of one
/// held from when a mark was made, so marks made under one answer and read under a later one are
/// judged again: <see cref="Standing"/> is what a confirmation and a removal read, and it drops a
/// mark that no longer holds, and every mark in a group that would no longer keep a copy.</para>
///
/// <para>Copies are known by their identity, never their path, because two copies can differ only
/// in the case of a letter.</para>
/// </summary>
public sealed class GroupMarks
{
    private readonly HashSet<FileIdentity> _marked = [];

    public GroupMarks(DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        Group = group;
    }

    public DuplicateGroup Group { get; }

    /// <summary>Whether <paramref name="copy"/> is marked.</summary>
    public bool IsMarked(DuplicateCandidate copy) => _marked.Contains(copy.Identity);

    /// <summary>How many copies are marked, standing or not.</summary>
    public int MarkedCount => _marked.Count;

    /// <summary>
    /// Mark <paramref name="copy"/>, or say why not: it is never marked, it is refused, or no other
    /// copy that can be kept would be left unmarked. Null where it is marked.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="copy"/> is not in this group.</exception>
    public string? Mark(DuplicateCandidate copy, CopyKeeping keeping)
    {
        ArgumentNullException.ThrowIfNull(keeping);
        Member(copy);

        if (keeping.WhyNotMarked(copy) is { } why)
        {
            return why;
        }

        if (WhyNothingCanBeKept(keeping) is { } nothing)
        {
            return nothing;
        }

        if (!Group.Files.Any(other => other.Identity != copy.Identity && !IsMarked(other) && keeping.WhyNotKept(other) is null))
        {
            return "Marking this would leave no copy in the group that can be kept, and every group keeps one.";
        }

        _marked.Add(copy.Identity);

        return null;
    }

    /// <summary>Unmark <paramref name="copy"/>, which is always allowed.</summary>
    public void Unmark(DuplicateCandidate copy)
    {
        Member(copy);
        _marked.Remove(copy.Identity);
    }

    /// <summary>Unmark every copy.</summary>
    public void Clear() => _marked.Clear();

    /// <summary>
    /// Why no copy in the group can be kept, so every copy stays unmarked, or null where one can be.
    /// Each copy's own reason is <see cref="CopyKeeping.WhyNotKept"/>'s.
    /// </summary>
    public string? WhyNothingCanBeKept(CopyKeeping keeping)
    {
        ArgumentNullException.ThrowIfNull(keeping);

        return Group.Files.Any(copy => keeping.WhyNotKept(copy) is null) ? null : GroupJudgement.NothingKept;
    }

    /// <summary>
    /// The marks that hold now, which are what a confirmation lists and a removal takes: each marked
    /// copy that may still be marked, and none at all where they would leave no unmarked copy that
    /// can be kept.
    /// </summary>
    public IReadOnlyList<DuplicateCandidate> Standing(CopyKeeping keeping)
    {
        ArgumentNullException.ThrowIfNull(keeping);

        List<DuplicateCandidate> standing =
        [
            .. Group.Files.Where(copy =>
                IsMarked(copy) && keeping.WhyNotMarked(copy) is null),
        ];

        var keepsOne = Group.Files.Any(copy => !standing.Exists(marked => marked.Identity == copy.Identity) && keeping.WhyNotKept(copy) is null);

        return keepsOne ? standing : [];
    }

    /// <summary>
    /// What the standing marks occupy on disk, which a removal may free less than
    /// (<see cref="DuplicateCandidate.SizeOnDisk"/>).
    /// </summary>
    public long MarkedSpace(CopyKeeping keeping) => Standing(keeping).Sum(copy => copy.SizeOnDisk);

    private void Member(DuplicateCandidate copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (!Group.Files.Any(file => file.Identity == copy.Identity))
        {
            throw new ArgumentException("That copy is not in this group.", nameof(copy));
        }
    }
}
