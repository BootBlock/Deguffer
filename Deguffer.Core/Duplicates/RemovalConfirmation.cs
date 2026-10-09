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
/// copies removed earlier in the same run, so it says that too, and says where it could not tell.</para>
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
public sealed record RemovalConfirmation(
    IReadOnlyList<DuplicateCandidate> Copies,
    int Groups,
    long Space,
    string Summary,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The confirmation of removing the marks that stand in <paramref name="marks"/>.</summary>
    /// <param name="protections">Built afresh for this confirmation, so Explore's policy is read now.</param>
    /// <param name="room">The room in a volume's Recycle Bin, or null where Windows would not say.</param>
    public static async Task<RemovalConfirmation> ForAsync(
        DuplicateMarks marks,
        MachineProtections protections,
        ExploreRemovalMode mode,
        Func<LocalVolume, RecycleBinRoom?> room,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);

        return For(marks, await marks.RejudgeAsync(protections, ct).ConfigureAwait(false), mode, room);
    }

    /// <param name="keeping">The keeping rule as <see cref="DuplicateMarks.RejudgeAsync"/> judged it for this confirmation.</param>
    internal static RemovalConfirmation For(
        DuplicateMarks marks, CopyKeeping keeping, ExploreRemovalMode mode, Func<LocalVolume, RecycleBinRoom?> room)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(keeping);
        ArgumentNullException.ThrowIfNull(room);

        List<IReadOnlyList<DuplicateCandidate>> byGroup = [.. marks.Groups.Select(group => group.Standing(keeping)).Where(standing => standing.Count > 0)];
        List<DuplicateCandidate> copies = [.. byGroup.SelectMany(standing => standing)];
        var space = copies.Sum(copy => copy.SizeOnDisk);
        var toBin = mode == ExploreRemovalMode.RecycleBin;

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

        return new RemovalConfirmation(copies, byGroup.Count, space, summary, warnings);
    }

    /// <summary>
    /// A sentence for each drive whose bin cannot be shown to take what goes to it. The bin is
    /// measured by length, which is what Windows counts against its limit.
    /// </summary>
    private static IEnumerable<string> BinWarnings(IReadOnlyList<DuplicateCandidate> copies, Func<LocalVolume, RecycleBinRoom?> room)
    {
        foreach (var drive in copies.GroupBy(copy => copy.Volume.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            var bound = drive.Sum(copy => copy.Length);
            var name = drive.Key;

            switch (room(drive.First().Volume))
            {
                case null:
                    yield return $"Windows would not say how much the Recycle Bin on {name} holds, so Deguffer cannot tell "
                        + "whether these copies fit. Where they do not, Windows deletes the bin's oldest items outright to make room.";
                    break;

                case { KeepsNothing: true }:
                    yield return $"Windows is set to delete files on {name} rather than keep them in its Recycle Bin, so the "
                        + "copies there cannot go to the bin, and are not removed.";
                    break;

                case { Free: null }:
                    yield return $"Deguffer could not read how large the Recycle Bin on {name} may grow, so it cannot tell "
                        + "whether these copies fit. Where they do not, Windows deletes the bin's oldest items outright to make room.";
                    break;

                case { Free: { } free } when bound > free:
                    yield return $"The copies bound for the Recycle Bin on {name} ({FreeSpace.Format(bound)}) are more than it has "
                        + $"room for ({FreeSpace.Format(free)}). Windows then deletes the bin's oldest items outright to make room, "
                        + "and those can include copies removed earlier in this removal.";
                    break;
            }
        }
    }

    private static string Count(int count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";
}
