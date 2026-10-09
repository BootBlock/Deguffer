using Deguffer.Core.Cloud;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>What one rule did: how many copies it marked, and each group it marked nothing in, with why.</summary>
/// <param name="Refused">Why the rule marked nothing anywhere, or null where it ran.</param>
public sealed record RuleOutcome(int Marked, IReadOnlyList<(GroupMarks Group, string Reason)> Untouched, string? Refused = null)
{
    /// <summary>What the rule did, in a sentence for the page: why it ran nowhere, or what it marked and what it left.</summary>
    public string Summary
    {
        get
        {
            if (Refused is { } refused)
            {
                return refused;
            }

            var marked = Marked switch
            {
                0 => "The rule marked nothing.",
                1 => "The rule marked 1 copy.",
                _ => $"The rule marked {Marked:N0} copies.",
            };

            return Untouched.Count switch
            {
                0 => marked,
                1 => $"{marked} It left 1 group as it was, which says why.",
                _ => $"{marked} It left {Untouched.Count:N0} groups as they were, each saying why.",
            };
        }
    }
}

/// <summary>
/// The marks on every group of one search (§7.4), and the rules that make them.
///
/// <para><b>Nothing is marked when a search finishes.</b> The user marks by hand, or runs a named
/// rule (<see cref="MarkingRule"/>).</para>
///
/// <para><b>No rule marks while a reference went unsearched.</b> Where a location chosen as a
/// reference was not searched (<see cref="CandidateFinding.UnsearchedReferences"/>), for whatever
/// reason, Deguffer cannot tell which copies it
/// holds: a link on the way to it, or a share that names this computer's own disk, can put its copies
/// inside a searched location under another name, in the search role. Marking by hand stays open,
/// because each such mark is one the user looked at.</para>
///
/// <para><b>Nothing reads the groups off the thread that adds them until the search has ended.</b>
/// Groups are added on the page's thread as the search confirms them (<see cref="Add"/>), while a
/// rule, a confirmation and a removal read them on another. So a rule
/// (<see cref="Run"/>) and a judgement (<see cref="RejudgeAsync"/>, which every confirmation and
/// removal begins with) refuse to start until the thread that adds the groups says the last one is
/// in (<see cref="Complete"/>), and no group is added after it.</para>
/// </summary>
public sealed class DuplicateMarks
{
    private const string ReferenceUnsearched =
        "A location chosen as a reference was not searched, so Deguffer cannot tell which copies it holds, "
        + "and no rule marks anything until it is. Each one says why it was not searched.";

    private readonly CandidateFinding _finding;
    private readonly KeepingReader _reader;
    private readonly List<GroupMarks> _groups = [];
    private volatile bool _complete;

    private DuplicateMarks(
        CandidateFinding finding, ExploreActionPolicy policy, IReadOnlyList<StorageClean> cleans, KeepingReader reader)
    {
        _finding = finding;
        _reader = reader;
        UnsearchedReferences = finding.UnsearchedReferences;

        // Every copy a group can hold is among the candidates, so each of their drives is asked here,
        // before the first group arrives, and never as one is placed.
        Keeping = reader.AsSearched(policy, cleans, finding.ProgramFolders, finding.Groups.SelectMany(group => group.Files));
    }

    /// <summary>
    /// Every group added, the one that could free the most first as the keeping rule was judged when
    /// the marks were made, and groups that could free the same in the order they were added. Each is
    /// placed as it is added and never moved, and a later <see cref="RejudgeAsync"/> does not reorder
    /// them, so a page's list keeps the reader's place.
    /// </summary>
    public IReadOnlyList<GroupMarks> Groups => _groups;

    /// <summary>
    /// What decides which copies can be kept and which are refused, as it was judged last: when the
    /// marks were made, or at the last <see cref="RejudgeAsync"/>.
    /// </summary>
    public CopyKeeping Keeping { get; private set; }

    /// <summary>The reference locations that went unsearched, which stop every rule.</summary>
    public IReadOnlyList<UnsearchedLocation> UnsearchedReferences { get; }

    /// <summary>
    /// The marks for the search that found <paramref name="finding"/>, holding no group until each is
    /// added as the search confirms it (<see cref="Add"/>), with everything that decides which copies
    /// can be kept read now: where Storage's cleans delete, the temporary folder, the cloud folders,
    /// and what each candidate's drive is. Made as soon as the candidates are found
    /// (<see cref="DuplicateSearcher.SearchAsync"/>), so each group can be placed by the space it
    /// could free as it arrives. Blocks on each volume's disks, so never call it on the UI thread.
    /// </summary>
    /// <param name="protections">The same protections whose policy the search passed over places by.</param>
    /// <param name="media">
    /// The cache the search read the drives through, whose answers these marks take, and whose
    /// <see cref="VolumeMediaCache.Now"/> a confirmation asks again.
    /// </param>
    public static async Task<DuplicateMarks> ForAsync(
        CandidateFinding finding,
        MachineProtections protections,
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        VolumeMediaCache media,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protections);
        ArgumentNullException.ThrowIfNull(media);

        return For(
            finding,
            protections.Policy,
            await protections.StorageCleansAsync(ct).ConfigureAwait(false),
            environment,
            cloud,
            volumes,
            media.Of,
            media.Now,
            FileInformation.Default);
    }

    /// <param name="searchedMedia">What a volume's disks are as the search found them, so a test can stand for a USB disk that says it is fixed.</param>
    /// <param name="mediaNow">What a volume's disks are when a confirmation asks again.</param>
    /// <param name="files">Where each place is followed to its final path, so a test can stand for a junction.</param>
    internal static DuplicateMarks For(
        CandidateFinding finding,
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        Func<LocalVolume, VolumeMedia> searchedMedia,
        Func<LocalVolume, VolumeMedia> mediaNow,
        FileInformation files)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(cleans);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(volumes);

        return new DuplicateMarks(finding, policy, cleans, new KeepingReader(environment, cloud, volumes, searchedMedia, mediaNow, files));
    }

    /// <summary>
    /// Add a group the search confirmed, placed after every group that could free more or the same,
    /// as the keeping rule was judged when the marks were made, and before every group that could
    /// free less. Call it on one thread, the one that reads <see cref="Groups"/> while the search
    /// runs.
    /// </summary>
    /// <returns>The marks for the group, and where in <see cref="Groups"/> they were placed.</returns>
    /// <exception cref="InvalidOperationException">The search was said to have ended (<see cref="Complete"/>).</exception>
    public (GroupMarks Marks, int Index) Add(DuplicateGroup group)
    {
        if (_complete)
        {
            throw new InvalidOperationException("The search has ended, so no group is added to its marks.");
        }

        var marks = new GroupMarks(group);
        var space = marks.FreeableSpace(Keeping);

        // Before the first group that could free less, so groups that could free the same stay in
        // the order the search confirmed them.
        var index = _groups.FindIndex(placed => placed.FreeableSpace(Keeping) < space);

        if (index < 0)
        {
            index = _groups.Count;
        }

        _groups.Insert(index, marks);

        return (marks, index);
    }

    /// <summary>Whether the search has ended, so a rule, a confirmation and a removal may read the groups.</summary>
    public bool IsComplete => _complete;

    /// <summary>
    /// Say the search has ended and its last group is added, on the thread that added them, so a
    /// rule, a confirmation and a removal may read them on another. Stopped or not, every group it
    /// confirmed is in.
    /// </summary>
    public void Complete() => _complete = true;

    /// <summary>
    /// Judge the marks again against the machine as it is now, keeping every mark: Explore's policy,
    /// Storage's places and the program folders from <paramref name="protections"/>, and the
    /// temporary folder, the cloud folders and each drive's disks read again. A confirmation is built
    /// from the answer (<see cref="RemovalConfirmation.ForAsync"/>), because a folder can become the
    /// temporary folder, a cache, a cloud folder or a program's folder, and a disk can move to a USB
    /// dock, between the marks and the removal, and a mark made before then would leave a group
    /// keeping only a copy that can now go without anyone choosing it to. A program folder the search
    /// knew stays one, because a list of programs Windows will not read now names nothing. Blocks on
    /// the disks and the registry, so never call it on the UI thread.
    /// </summary>
    /// <param name="protections">Built afresh for this question, so what the machine protects is read now.</param>
    /// <exception cref="InvalidOperationException">The search has not ended (<see cref="Complete"/>).</exception>
    public async Task<CopyKeeping> RejudgeAsync(MachineProtections protections, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protections);
        ThrowUnlessComplete();

        var cleans = await protections.StorageCleansAsync(ct).ConfigureAwait(false);
        var programs = _reader.ProgramsNow(protections, _finding.ProgramFolders, ct);

        return Keeping = _reader.Now(protections.Policy, cleans, programs, _groups.SelectMany(group => group.Group.Files));
    }

    /// <summary>Why no rule can mark anything, or null where rules may run.</summary>
    public string? WhyRulesCannotMark => UnsearchedReferences.Count > 0 ? ReferenceUnsearched : null;

    /// <summary>
    /// Run <paramref name="rule"/> over every group, adding to the marks there are. A rule that names a
    /// folder opens it to follow it to its final path, so never call it on the UI thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The search has not ended (<see cref="Complete"/>).</exception>
    public RuleOutcome Run(MarkingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ThrowUnlessComplete();

        if (WhyRulesCannotMark is { } blocked)
        {
            return new RuleOutcome(0, [], blocked);
        }

        var folder = rule switch
        {
            MarkingRule.KeepInFolder keepIn => _reader.Folder(keepIn.Folder),
            MarkingRule.MarkInFolder markIn => _reader.Folder(markIn.Folder),
            _ => null,
        };

        var marked = 0;
        List<(GroupMarks, string)> untouched = [];

        foreach (var group in Groups)
        {
            var (count, why) = Apply(rule, group, folder);
            marked += count;

            if (count == 0 && why is not null)
            {
                untouched.Add((group, why));
            }
        }

        return new RuleOutcome(marked, untouched);
    }

    /// <summary>Refuses to read the groups off the thread that adds them while it may still be adding one.</summary>
    internal void ThrowUnlessComplete()
    {
        if (!_complete)
        {
            throw new InvalidOperationException(
                "The search is still adding groups, so no rule, confirmation or removal may read them yet.");
        }
    }

    private (int Marked, string? Why) Apply(MarkingRule rule, GroupMarks group, ResolvedPlaces? folder)
    {
        var files = group.Group.Files;
        List<DuplicateCandidate> keepable = [.. files.Where(copy => !group.IsMarked(copy) && Keeping.WhyNotKept(copy) is null)];

        if (keepable.Count == 0)
        {
            return (0, group.WhyNothingCanBeKept(Keeping)
                ?? "No unmarked copy here can be kept, so the rule marks nothing.");
        }

        bool InFolder(DuplicateCandidate copy) => copy.Names.Any(name => folder!.WhatHolds(name) is not null);

        IReadOnlyList<DuplicateCandidate> toMark;

        switch (rule)
        {
            case MarkingRule.MarkInFolder:
                toMark = [.. files.Where(InFolder)];

                if (!keepable.Exists(copy => !toMark.Contains(copy)))
                {
                    return (0, "Every copy here that can be kept is in that folder, so the rule marks nothing.");
                }

                break;

            case MarkingRule.KeepInFolder:
                List<DuplicateCandidate> keptThere = [.. keepable.Where(InFolder)];

                if (keptThere.Count == 0)
                {
                    return (0, "No copy here that can be kept is in that folder, so the rule marks nothing.");
                }

                toMark = [.. files.Where(copy => !keptThere.Contains(copy))];
                break;

            default:
                var kept = Chosen(rule, keepable);
                toMark = [.. files.Where(copy => copy.Identity != kept.Identity)];
                break;
        }

        var marked = 0;

        foreach (var copy in toMark)
        {
            if (!group.IsMarked(copy) && Keeping.CloudFolderOf(copy) is null && group.Mark(copy, Keeping) is null)
            {
                marked++;
            }
        }

        return (marked, marked == 0 ? "Nothing here is a copy the rule may mark." : null);
    }

    /// <summary>The one copy a keep rule keeps, ties broken by the shorter path and then the path's text.</summary>
    private static DuplicateCandidate Chosen(MarkingRule rule, IReadOnlyList<DuplicateCandidate> keepable)
    {
        var ordered = rule switch
        {
            MarkingRule.KeepNewest => keepable.OrderByDescending(copy => copy.Modified),
            MarkingRule.KeepOldest => keepable.OrderBy(copy => copy.Modified),
            MarkingRule.KeepShortestPath => keepable.OrderBy(copy => 0),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Not a rule that keeps one copy."),
        };

        return ordered.ThenBy(copy => copy.Path.Length).ThenBy(copy => copy.Path, StringComparer.Ordinal).First();
    }
}
