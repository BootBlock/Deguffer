using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Exploring;

/// <summary>One folder of a scan of several, and where in the request it was asked for.</summary>
/// <param name="Volume">The folder parsed for the file table's route, or null where that route cannot serve it.</param>
internal readonly record struct FolderAsked(int Index, VolumePath? Volume);

/// <summary>
/// The folders asked for on one volume, whose file table one read answers for every one of them, or a
/// single folder on no volume a table can be read for.
/// </summary>
/// <param name="DriveLetter">The volume's letter, or null where only the walk can answer.</param>
internal sealed record VolumeFolders(char? DriveLetter, IReadOnlyList<FolderAsked> Folders);

/// <summary>
/// Puts the folders of a scan of several into pipelines, one for each set of physical disks, so a
/// pipeline's table reads and walks can run at the same time as another's (#369).
///
/// <para><b>Volumes on one disk stay in one pipeline</b>, one after another
/// (<see cref="DiskSharing"/>). A walk or a table read on each of two volumes of one spinning disk
/// would make it seek between them for the length of both.</para>
///
/// <para><b>Apart only where known to be apart.</b> A volume whose disks are not listed might be on
/// any disk: a <c>subst</c> letter Windows would not describe, a volume that would not open, a tuner
/// built without the machine's devices. Where any is, every volume goes in one pipeline, which is how
/// every search ran before. A share, or an optical drive, is on no disk of this machine, and is
/// apart from every other.</para>
///
/// <para><b>A volume's disks are those of its top and of every folder asked for on it.</b> A folder
/// can be a mount point of a volume on another disk, which its walk reads, while the table read is
/// of the volume at the letter. What a walk meets below a folder is not known until it is walked, so
/// a mount point deeper down is not seen here.</para>
/// </summary>
internal static class FolderPipelines
{
    /// <param name="storageOf">What a path is on, or null where that is not known (<see cref="ScanTuner.StorageOf"/>).</param>
    /// <returns>
    /// Each pipeline's volumes, in the order work on them is to run. Pipelines and the volumes in each
    /// come in the order their first folder was asked for, so a scan with one pipeline runs in the
    /// order of the request.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<VolumeFolders>> Of(IReadOnlyList<string> folders, Func<string, VolumeMedia?> storageOf)
    {
        List<(char? DriveLetter, List<FolderAsked> Folders, List<string> Paths)> volumes = [];
        Dictionary<char, int> onLetter = [];

        for (var i = 0; i < folders.Count; i++)
        {
            if (!VolumePath.TryParse(folders[i], out var parsed))
            {
                volumes.Add((null, [new FolderAsked(i, null)], [folders[i]]));
                continue;
            }

            if (!onLetter.TryGetValue(parsed.DriveLetter, out var at))
            {
                onLetter[parsed.DriveLetter] = at = volumes.Count;
                volumes.Add((parsed.DriveLetter, [], [$@"{parsed.DriveLetter}:\"]));
            }

            volumes[at].Folders.Add(new FolderAsked(i, parsed));
            volumes[at].Paths.Add(folders[i]);
        }

        VolumeFolders[] asked = [.. volumes.Select(volume => new VolumeFolders(volume.DriveLetter, volume.Folders))];
        var disks = new IReadOnlyList<int>[volumes.Count];

        for (var i = 0; i < volumes.Count; i++)
        {
            if (DisksUnder(volumes[i].Paths, storageOf) is not { } under)
            {
                return [asked];
            }

            disks[i] = under;
        }

        return
        [
            .. DiskSharing.Sets([.. Enumerable.Range(0, asked.Length)], i => disks[i])
                .Select(set => (IReadOnlyList<VolumeFolders>)[.. set.Select(i => asked[i])]),
        ];
    }

    /// <summary>
    /// Every disk under any of <paramref name="paths"/>, or null where one of them might be on any
    /// disk. Empty for paths on no disk of this machine.
    /// </summary>
    private static IReadOnlyList<int>? DisksUnder(IReadOnlyList<string> paths, Func<string, VolumeMedia?> storageOf)
    {
        SortedSet<int> under = [];

        foreach (var path in paths)
        {
            // Empty disks with no reason is a share or an optical drive, which are not asked. Empty
            // with a reason is a volume that did not say.
            if (storageOf(path) is not { } media || media is { PhysicalDisks.Count: 0, Reason: not MediaUnknownReason.None })
            {
                return null;
            }

            under.UnionWith(media.PhysicalDisks);
        }

        return [.. under];
    }
}
