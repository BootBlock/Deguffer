using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Cloud;

/// <summary>
/// Every sync root to be walked, with one <see cref="PlaceholderWalk"/> for each volume the roots are
/// on, and the walks of volumes on separate disks run at the same time (#1071).
///
/// <para><b>One walk for each volume, never two.</b> The walk takes no slot of
/// <see cref="ListingBudget"/>, so a walk for each of two roots on one volume, run at once, would read
/// it with twice the threads it is tuned for. The roots on one volume are walked in one run instead,
/// sharing its threads, which spares the second walk's start and tail.</para>
///
/// <para><b>Volumes are placed on disks as a scan's folders are</b>
/// (<see cref="FolderPipelines.ByDisks{T}"/>). Volumes on separate disks are walked at once, so two
/// roots there cost the longer walk rather than the sum. Two volumes of one disk are walked in turn,
/// as a walk of each at once would make a spinning disk seek between them. Where a volume's disks are
/// not known, every volume is walked in turn, as every root was before.</para>
/// </summary>
internal static class PlaceholderWalks
{
    /// <summary>
    /// What each of <paramref name="syncRoots"/> holds, in the order given. Asks what each root is on,
    /// which opens a device the first time a volume is met, on a worker thread rather than the caller's.
    /// </summary>
    /// <param name="tuning">What the walk of each volume runs with.</param>
    public static async Task<IReadOnlyList<ReleaseSelection>> OfAsync(
        ICloudFiles cloud, IReadOnlyList<string> syncRoots, MinimumAge keep, ScanTuner tuning, CancellationToken ct)
    {
        if (syncRoots.Count == 0)
        {
            return [];
        }

        var pipelines = await WorkerThreads.Shared.RunAsync(() => Pipelines(syncRoots, tuning), ct).ConfigureAwait(false);
        var selections = new ReleaseSelection[syncRoots.Count];

        await PipelinesAtOnce.RunAsync(
                pipelines,
                async (volumes, token) =>
                {
                    foreach (var volume in volumes)
                    {
                        var found = await WorkerThreads.Shared.RunAsync(
                                () => PlaceholderWalk.Of(
                                    cloud, [.. volume.Roots.Select(at => syncRoots[at])], keep, volume.Threads, token),
                                token)
                            .ConfigureAwait(false);

                        for (var i = 0; i < found.Count; i++)
                        {
                            selections[volume.Roots[i]] = found[i];
                        }
                    }
                },
                ct)
            .ConfigureAwait(false);

        return selections;
    }

    /// <summary>
    /// The roots by volume, each volume with its walk's threads, and the volumes in sets that may be
    /// walked at once. Each root is asked about once.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<VolumeRoots>> Pipelines(IReadOnlyList<string> syncRoots, ScanTuner tuning)
    {
        var storages = tuning.ForOneMeasurement();
        var places = new PathStorage?[syncRoots.Count];
        var volumes = new List<VolumeRoots>();
        var byVolume = new Dictionary<string, VolumeRoots>(StringComparer.OrdinalIgnoreCase);

        for (var at = 0; at < syncRoots.Count; at++)
        {
            var place = places[at] = storages.PlaceOf(syncRoots[at]);

            // A tuner that knows no volumes, and a share, name none, and the path's own root then
            // stands for it.
            var volume = place?.VolumeRoot ?? Path.GetPathRoot(syncRoots[at]) ?? syncRoots[at];

            if (!byVolume.TryGetValue(volume, out var roots))
            {
                byVolume[volume] = roots = new VolumeRoots(tuning.For(place?.Media).Walk.Threads, []);
                volumes.Add(roots);
            }

            roots.Roots.Add(at);
        }

        return FolderPipelines.ByDisks(volumes, volume => volume.Roots.Select(at => places[at]?.Media));
    }

    /// <summary>The roots on one volume, by their index in the list asked for, and how many threads its walk runs on.</summary>
    private sealed record VolumeRoots(int Threads, List<int> Roots);
}
