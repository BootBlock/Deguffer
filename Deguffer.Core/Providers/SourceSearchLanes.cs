using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Providers;

/// <summary>
/// Puts the approved folders a source discovery searches into lanes. The folders of one lane are
/// searched one after another, and the lanes at the same time (#471).
///
/// <para><b>Apart where their disks are apart</b>, by <see cref="FolderPipelines"/>, which a scan of
/// several folders uses for the same reason: folders on separate disks share nothing a search waits
/// on. Where the disks of any folder are not known, every folder is in one lane, which is how every
/// discovery ran before.</para>
///
/// <para><b>Together on a solid-state disk.</b> A small folder's walk takes about its depth times one
/// listing, with most of the walk's threads idle, so folders searched in turn wait the sum of their
/// depths. Walks of one volume share its listings through <see cref="Scanning.ListingBudget"/>, so
/// several at once never ask the volume for more listings than one walk was tuned for. On a disk with
/// a seek penalty, or one not known to be solid state, the walks would interleave two trees under one
/// head, so its folders stay in one lane.</para>
/// </summary>
internal static class SourceSearchLanes
{
    /// <param name="storageOf">What a path is on, or null where that is not known (<see cref="Scanning.ScanTuner.StorageOf"/>).</param>
    /// <returns>Each lane's folders, by their index in <paramref name="folders"/>, in the order asked.</returns>
    public static IReadOnlyList<IReadOnlyList<int>> Of(IReadOnlyList<string> folders, Func<string, VolumeMedia?> storageOf)
    {
        List<IReadOnlyList<int>> lanes = [];

        foreach (var pipeline in FolderPipelines.Of(folders, storageOf))
        {
            int[] inTurn = [.. pipeline.SelectMany(volume => volume.Folders).Select(folder => folder.Index).Order()];

            if (inTurn.All(index => IsSolidState(folders[index], storageOf)))
            {
                lanes.AddRange(inTurn.Select(index => (IReadOnlyList<int>)[index]));
            }
            else
            {
                lanes.Add(inTurn);
            }
        }

        return lanes;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> and the top of its volume are both known to be on solid state
    /// (<see cref="VolumeMedia.IsKnownSolidState"/>). The folder can be a mount point of another
    /// volume, which its walk reads, while the volume index is read from the volume at the top.
    /// </summary>
    private static bool IsSolidState(string folder, Func<string, VolumeMedia?> storageOf) =>
        storageOf(folder) is { IsKnownSolidState: true }
        && (Path.GetPathRoot(folder) is not { Length: > 0 } top || storageOf(top) is { IsKnownSolidState: true });
}
