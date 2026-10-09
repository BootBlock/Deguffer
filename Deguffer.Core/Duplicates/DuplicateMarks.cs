using Deguffer.Core.Cloud;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>What one rule did: how many copies it marked, and each group it marked nothing in, with why.</summary>
/// <param name="Refused">Why the rule marked nothing anywhere, or null where it ran.</param>
public sealed record RuleOutcome(int Marked, IReadOnlyList<(GroupMarks Group, string Reason)> Untouched, string? Refused = null);

/// <summary>
/// The marks on every group of one search (§7.4), and the rules that make them.
///
/// <para><b>Nothing is marked when a search finishes.</b> The user marks by hand, or runs a named
/// rule (<see cref="MarkingRule"/>).</para>
///
/// <para><b>No rule marks while a reference went unsearched.</b> Where a location chosen as a
/// reference was not searched in whole, for whatever reason, Deguffer cannot tell which copies it
/// holds: a link on the way to it, or a share that names this computer's own disk, can put its copies
/// inside a searched location under another name, in the search role. Marking by hand stays open,
/// because each such mark is one the user looked at.</para>
/// </summary>
public sealed class DuplicateMarks
{
    private const string ReferenceUnsearched =
        "A location chosen as a reference was not searched, so Deguffer cannot tell which copies it holds, "
        + "and no rule marks anything until it is. Each one says why it was not searched.";

    private readonly DuplicateSearchResult _result;
    private readonly IUserEnvironment _environment;
    private readonly ICloudFiles _cloud;
    private readonly IVolumeInventory _volumes;
    private readonly Func<LocalVolume, VolumeMedia> _media;
    private readonly FileInformation _files;

    private DuplicateMarks(
        DuplicateSearchResult result,
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        Func<LocalVolume, VolumeMedia> media,
        FileInformation files)
    {
        _result = result;
        _environment = environment;
        _cloud = cloud;
        _volumes = volumes;
        _media = media;
        _files = files;
        UnsearchedReferences = result.Finding.UnsearchedReferences;
        Keeping = Judge(policy, cleans);

        // Sorted once, by the space each group could free, which a mark does not change.
        Groups = [.. result.Groups.Select(group => new GroupMarks(group)).OrderByDescending(marks => marks.FreeableSpace(Keeping))];
    }

    /// <summary>Every group, the one that could free the most first.</summary>
    public IReadOnlyList<GroupMarks> Groups { get; }

    /// <summary>
    /// What decides which copies can be kept and which are refused, as it was judged last: when the
    /// marks were made, or at the last <see cref="RejudgeAsync"/>.
    /// </summary>
    public CopyKeeping Keeping { get; private set; }

    /// <summary>The reference locations that went unsearched, which stop every rule.</summary>
    public IReadOnlyList<UnsearchedLocation> UnsearchedReferences { get; }

    /// <summary>
    /// The marks for <paramref name="result"/>, with everything that decides which copies can be kept
    /// read now: where Storage's cleans delete, the temporary folder, the cloud folders, and what each
    /// volume's disks are. Blocks on each volume's disks the first time it is asked, so never call it
    /// on the UI thread.
    /// </summary>
    /// <param name="protections">The same protections whose policy the search passed over places by.</param>
    public static async Task<DuplicateMarks> ForAsync(
        DuplicateSearchResult result,
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
            result,
            protections.Policy,
            await protections.StorageCleansAsync(ct).ConfigureAwait(false),
            environment,
            cloud,
            volumes,
            media.Of,
            FileInformation.Default);
    }

    /// <param name="media">What a volume's disks are, so a test can stand for a USB disk that says it is fixed.</param>
    /// <param name="files">Where each place is followed to its final path, so a test can stand for a junction.</param>
    internal static DuplicateMarks For(
        DuplicateSearchResult result,
        ExploreActionPolicy policy,
        IReadOnlyList<StorageClean> cleans,
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        Func<LocalVolume, VolumeMedia> media,
        FileInformation files)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(cleans);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(volumes);

        return new DuplicateMarks(result, policy, cleans, environment, cloud, volumes, media, files);
    }

    /// <summary>
    /// Judge the marks again against the machine as it is now, keeping every mark: Explore's policy
    /// and Storage's places from <paramref name="protections"/>, and the temporary folder, the cloud
    /// folders and each volume's disks read again. A confirmation is built from the answer
    /// (<see cref="RemovalConfirmation.ForAsync"/>), because a folder can become the temporary folder,
    /// a cache, a cloud folder or a program's folder between the marks and the removal, and a mark
    /// made before then would leave a group keeping only a copy that can now go without anyone
    /// choosing it to. Blocks on the disks, so never call it on the UI thread.
    /// </summary>
    /// <param name="protections">Built afresh for this question, so Explore's policy is read now.</param>
    public async Task<CopyKeeping> RejudgeAsync(MachineProtections protections, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protections);

        return Rejudge(protections.Policy, await protections.StorageCleansAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Judge the marks again against <paramref name="policy"/> and <paramref name="cleans"/>, and everything else read now.</summary>
    internal CopyKeeping Rejudge(ExploreActionPolicy policy, IReadOnlyList<StorageClean> cleans)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(cleans);

        return Keeping = Judge(policy, cleans);
    }

    private CopyKeeping Judge(ExploreActionPolicy policy, IReadOnlyList<StorageClean> cleans)
    {
        // Asked once a volume here, off the UI thread, so the questions a page asks as it draws never
        // wait on a device.
        Dictionary<string, StorageMedia> classes = new(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in _result.Groups.SelectMany(group => group.Files).Select(copy => copy.Volume).Distinct())
        {
            classes[volume.RootPath] = _media(volume).Class;
        }

        var temporary = ResolvedPlaces.Resolve(
            [(CleanedPlace.Whole(_environment.TempPath),
              "This is in your temporary folder, which programs and Storage empty, so it is not counted on as the copy kept.")],
            _volumes,
            _files);

        var cleaned = ResolvedPlaces.Resolve(
            cleans.Select(clean => (clean.Place,
                $"Storage's '{clean.Clean}' clean can delete what is here, so it is not counted on as the copy kept.")),
            _volumes,
            _files);

        var cloudFolders = _cloud.SyncRoots() is { } roots
            ? ResolvedPlaces.Resolve(
                roots.Select(root => (CleanedPlace.Whole(root.Path),
                    $"This is in '{root.DisplayName}', a cloud folder: removing it removes it from every device that "
                    + "syncs the folder, and another device can remove it from here.")),
                _volumes,
                _files)
            : null;

        return new CopyKeeping(
            new CopyRefusals(policy, _result.Finding.ProgramFolders),
            temporary,
            cleaned,
            cloudFolders,
            volume => classes.TryGetValue(volume.RootPath, out var known) ? known : StorageMedia.Unknown);
    }

    /// <summary>Why no rule can mark anything, or null where rules may run.</summary>
    public string? WhyRulesCannotMark => UnsearchedReferences.Count > 0 ? ReferenceUnsearched : null;

    /// <summary>Run <paramref name="rule"/> over every group, adding to the marks there are.</summary>
    public RuleOutcome Run(MarkingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (WhyRulesCannotMark is { } blocked)
        {
            return new RuleOutcome(0, [], blocked);
        }

        var folder = rule switch
        {
            MarkingRule.KeepInFolder keepIn => Folder(keepIn.Folder),
            MarkingRule.MarkInFolder markIn => Folder(markIn.Folder),
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

    private ResolvedPlaces Folder(string folder) =>
        ResolvedPlaces.Resolve([(CleanedPlace.Whole(folder), folder)], _volumes, _files);
}
