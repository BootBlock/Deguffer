using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning;

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
/// Puts the folders of a scan of several, or the paths of a measurement of several, into pipelines,
/// one for each set of physical disks, so a pipeline's table reads and walks can run at the same time
/// as another's (#369, #470). <see cref="PipelinesAtOnce"/> runs them.
///
/// <para><b>Volumes on one disk stay in one pipeline</b>, one after another
/// (<see cref="DiskSharing"/>). A walk or a table read on each of two volumes of one spinning disk
/// would make it seek between them for the length of both.</para>
///
/// <para><b>Apart only where known to be apart.</b> A volume whose disks are not listed might be on
/// any disk: a <c>subst</c> letter Windows would not describe, a volume that would not open, a tuner
/// built without the machine's devices. Where any is, every volume goes in one pipeline, which is how
/// every search ran before. An optical drive is on no disk of this machine, and is apart from every
/// other.</para>
///
/// <para><b>Everything on the network is one pipeline.</b> A share, or a letter mapped to one, is on
/// no disk of this machine either, but two of them can be folders of one share, or shares of one
/// server on one set of disks, and nothing here says which. So they are scanned in turn, as if on
/// one disk (<see cref="Network"/>), and a search over many folders of one share walks one at a
/// time.</para>
///
/// <para><b>A volume's disks are those of its top and of every folder asked for on it.</b> A folder
/// can be a mount point of a volume on another disk, which its walk reads, while the table read is
/// of the volume at the letter. What a walk meets below a folder is not known until it is walked, so
/// a mount point deeper down is not seen here.</para>
/// </summary>
internal static class FolderPipelines
{
    /// <summary>
    /// The disk everything on the network is taken to share: a number no physical disk has, as
    /// Windows numbers them from 0 (<see cref="VolumeMedia.PhysicalDisks"/>).
    /// </summary>
    internal const int Network = -1;

    /// <param name="storageOf">What a path is on, or null where that is not known (<see cref="ScanTuner.StorageOf"/>).</param>
    /// <returns>
    /// Each pipeline's volumes. Pipelines and the volumes in each come in the order their first folder
    /// was asked for.
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

        return
        [
            .. ByDisks(volumes, volume => volume.Paths, storageOf)
                .Select(set => (IReadOnlyList<VolumeFolders>)[.. set.Select(volume => new VolumeFolders(volume.DriveLetter, volume.Folders))]),
        ];
    }

    /// <summary>
    /// <paramref name="items"/> in sets that can be worked at the same time, each item on the disks of
    /// every one of its paths, by the rules this type's remarks give: apart only where known to be
    /// apart, and everything on the network in one set. A walk, which reads only the volume of the
    /// directory it is given, is placed by that directory alone (<see cref="DirectoryWalks"/>).
    /// </summary>
    /// <returns>Each set in the order of its first item, and the items in each in that order too.</returns>
    public static IReadOnlyList<IReadOnlyList<T>> ByDisks<T>(
        IReadOnlyList<T> items, Func<T, IEnumerable<string>> pathsOf, Func<string, VolumeMedia?> storageOf)
    {
        // One item is one set whatever it is on, so no device is opened to learn its disks.
        if (items.Count <= 1)
        {
            return [items];
        }

        var disks = new IReadOnlyList<int>[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            if (DisksUnder(pathsOf(items[i]), storageOf) is not { } under)
            {
                return [items];
            }

            disks[i] = under;
        }

        return
        [
            .. DiskSharing.Sets([.. Enumerable.Range(0, items.Count)], i => disks[i])
                .Select(set => (IReadOnlyList<T>)[.. set.Select(i => items[i])]),
        ];
    }

    /// <summary>
    /// Every disk under any of <paramref name="paths"/>, <see cref="Network"/> for those on the
    /// network, or null where one of them might be on any disk. Empty for an optical drive.
    /// </summary>
    private static IReadOnlyList<int>? DisksUnder(IEnumerable<string> paths, Func<string, VolumeMedia?> storageOf)
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

            if (media is { Class: StorageMedia.Network, PhysicalDisks.Count: 0 })
            {
                under.Add(Network);
            }

            under.UnionWith(media.PhysicalDisks);
        }

        return [.. under];
    }
}
