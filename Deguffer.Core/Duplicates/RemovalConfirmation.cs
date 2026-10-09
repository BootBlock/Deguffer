using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// What the confirmation of a duplicate removal says (§7.4), built in Core so the page only shows it.
///
/// <para><b>Every copy that goes is listed</b>, with how many there are, from how many groups, and
/// what they occupy. A catalogue such as a photo library can name a copy Deguffer cannot see is in
/// use, and the list is where the user can.</para>
///
/// <para><b>The list is the marks that stand now</b> (<see cref="GroupMarks.Standing"/>), judged by
/// the keeping rule as the machine is at the confirmation rather than when each mark was made
/// (<see cref="DuplicateMarks.RejudgeAsync"/>).</para>
///
/// <para><b>A copy in a cloud folder</b> goes from every device that syncs the folder, so the
/// confirmation says so. <b>Copies bound for one drive's Recycle Bin that are more than it has room
/// for</b> make Windows delete the bin's oldest items outright to make room, and those can include
/// copies removed earlier in the same run, so it says that too.</para>
///
/// <para><b>A copy the Recycle Bin cannot take does not go</b>, so it is listed apart, with why,
/// rather than among the copies that go: the removal refuses it on the same answer from the same bin
/// (<see cref="DuplicateRemover.WhyTheBinCannotTake"/>), because Windows deletes outright what its bin
/// cannot take. A removable drive with no bin, a bin Windows will not describe or whose limit it will
/// not say, a bin set to keep nothing, a copy longer than its bin may hold and a path too long for it
/// are each such a copy.</para>
/// </summary>
/// <param name="Copies">Every copy that goes, group by group.</param>
/// <param name="Groups">How many groups they come from.</param>
/// <param name="Space">
/// What they occupy on disk, as Windows reports it, which a removal may free less than: copies can
/// share clusters through block cloning or deduplication, and the Recycle Bin frees what it holds only
/// when it is emptied.
/// </param>
/// <param name="Summary">The sentence that heads the confirmation.</param>
/// <param name="Warnings">Each sentence about a cloud folder, the Recycle Bin, or a permanent removal.</param>
/// <param name="Mode">
/// How the copies go, which the removal takes from here, so what the user confirmed and what is done
/// cannot come to differ.
/// </param>
/// <param name="Staying">Each marked copy that stays because the Recycle Bin cannot take it, with why.</param>
public sealed record RemovalConfirmation(
    IReadOnlyList<DuplicateCandidate> Copies,
    int Groups,
    long Space,
    string Summary,
    IReadOnlyList<string> Warnings,
    ExploreRemovalMode Mode,
    IReadOnlyList<StayingCopy> Staying)
{
    /// <summary>The dialog's title, which says how many copies go and how.</summary>
    public string Title => Mode == ExploreRemovalMode.RecycleBin
        ? $"Move {Count(Copies.Count, "copy", "copies")} to the Recycle Bin?"
        : $"Permanently delete {Count(Copies.Count, "copy", "copies")}?";

    /// <summary>The button that confirms, which names what it does rather than saying yes.</summary>
    public string ConfirmLabel => Mode == ExploreRemovalMode.RecycleBin ? "Move to Recycle Bin" : "Delete permanently";

    /// <summary>The confirmation of removing the marks that stand in <paramref name="marks"/>.</summary>
    /// <param name="protections">Built afresh for this confirmation, so Explore's policy is read now.</param>
    /// <param name="room">The room in a volume's Recycle Bin, or null where Windows would not say.</param>
    /// <param name="binCannotTake">
    /// Why the Recycle Bin the removal will use cannot take a copy, or null where nothing shows it
    /// cannot (<see cref="DuplicateRemover.WhyTheBinCannotTake"/>). Asked only for a removal to the bin.
    /// </param>
    public static async Task<RemovalConfirmation> ForAsync(
        DuplicateMarks marks,
        MachineProtections protections,
        ExploreRemovalMode mode,
        Func<LocalVolume, RecycleBinRoom?> room,
        Func<DuplicateCandidate, string?> binCannotTake,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(binCannotTake);

        return For(marks, await marks.RejudgeAsync(protections, ct).ConfigureAwait(false), mode, room, binCannotTake);
    }

    /// <param name="keeping">The keeping rule as <see cref="DuplicateMarks.RejudgeAsync"/> judged it for this confirmation.</param>
    internal static RemovalConfirmation For(
        DuplicateMarks marks,
        CopyKeeping keeping,
        ExploreRemovalMode mode,
        Func<LocalVolume, RecycleBinRoom?> room,
        Func<DuplicateCandidate, string?> binCannotTake)
    {
        marks.ThrowUnlessOpen();

        var toBin = mode == ExploreRemovalMode.RecycleBin;
        List<StayingCopy> staying = [];
        List<IReadOnlyList<DuplicateCandidate>> byGroup = [];

        foreach (var standing in marks.Groups.Select(group => group.Standing(keeping)))
        {
            List<DuplicateCandidate> going = [];

            foreach (var copy in standing)
            {
                if (toBin && binCannotTake(copy) is { } why)
                {
                    staying.Add(new StayingCopy(copy, why));
                }
                else
                {
                    going.Add(copy);
                }
            }

            if (going.Count > 0)
            {
                byGroup.Add(going);
            }
        }

        List<DuplicateCandidate> copies = [.. byGroup.SelectMany(going => going)];
        var space = copies.Sum(copy => copy.SizeOnDisk);

        var summary = $"{Count(copies.Count, "copy", "copies")} from {Count(byGroup.Count, "group", "groups")} will be "
            + (toBin ? "moved to the Recycle Bin" : "removed permanently")
            + $". They occupy {FreeSpace.Format(space)} on disk, and the removal may free less: copies can share space, "
            + "as block-cloned and deduplicated copies do"
            + (toBin ? ", and the Recycle Bin frees what it holds only when it is emptied." : ".");

        List<string> warnings = [];

        foreach (var (cloudFolder, inIt) in copies
            .Select(copy => (Folder: keeping.CloudFolderOf(copy), Copy: copy))
            .Where(pair => pair.Folder is not null)
            .GroupBy(pair => pair.Folder!)
            .Select(folder => (folder.Key, folder.Count())))
        {
            warnings.Add($"{Count(inIt, "copy is", "copies are")} in a cloud folder, and other devices that sync it lose "
                + $"{(inIt == 1 ? "it" : "them")} too. {cloudFolder}");
        }

        if (mode == ExploreRemovalMode.Permanent)
        {
            warnings.Add("These copies are removed permanently rather than moved to the Recycle Bin, and cannot be restored from it.");
        }
        else
        {
            warnings.AddRange(BinWarnings(copies, room));
        }

        if (staying.Count > 0)
        {
            var them = staying.Count == 1 ? "it" : "them";
            warnings.Add($"{Count(staying.Count, "marked copy stays", "marked copies stay")} where "
                + $"{(staying.Count == 1 ? "it is" : "they are")}, because the Recycle Bin cannot take {them}, and Windows "
                + $"deletes outright what its bin cannot take. Each is listed with why. Delete permanently removes {them}.");
        }

        return new RemovalConfirmation(copies, byGroup.Count, space, summary, warnings, mode, staying);
    }

    /// <summary>
    /// A sentence for each drive whose bin has less room than the copies bound for it, measured by
    /// length, which is what Windows counts against its limit. Each copy here is one the bin can take
    /// on its own: where a drive has no bin, Windows will not describe it or say its limit, or it keeps
    /// nothing, its copies stay (<see cref="Staying"/>).
    /// </summary>
    private static IEnumerable<string> BinWarnings(IReadOnlyList<DuplicateCandidate> copies, Func<LocalVolume, RecycleBinRoom?> room)
    {
        foreach (var drive in copies.GroupBy(copy => copy.Volume.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            var bound = drive.Sum(copy => copy.Length);

            if (room(drive.First().Volume) is { Free: { } free } && bound > free)
            {
                yield return $"The copies bound for the Recycle Bin on {drive.Key} ({FreeSpace.Format(bound)}) are more than it has "
                    + $"room for ({FreeSpace.Format(free)}). Windows then deletes the bin's oldest items outright to make room, "
                    + "and those can include copies removed earlier in this removal.";
            }
        }
    }

    private static string Count(int count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";
}

/// <summary>A marked copy that stays because the Recycle Bin cannot take it.</summary>
/// <param name="Why">Why the bin cannot take it, as the bin answers.</param>
public sealed record StayingCopy(DuplicateCandidate Copy, string Why);
