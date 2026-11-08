namespace Deguffer.Core.Scanning;

/// <summary>
/// The directories of one measurement, as one walk takes them: every directory on drives tuned alike
/// shares a walk, because a walk runs with one set of values.
/// </summary>
/// <param name="Indexes">Where each directory was asked for in the measurement, in that order.</param>
internal readonly record struct DirectoryWalk(WalkTuning Tuning, IReadOnlyList<int> Indexes);

/// <summary>
/// Puts the directories a walking scanner measures into walks, and runs them: the walks on each set of
/// physical disks at the same time as those on every other set, and the walks on one set in turn
/// (#470). <see cref="ParallelEnumerationScanner"/> and <see cref="HardLinkAwareScanner"/> both walk
/// this way, so neither decides it.
///
/// <para><b>Sets by each directory's own disks</b>, by the rules of <see cref="FolderPipelines"/>
/// (<see cref="FolderPipelines.ByDisks"/>). A walk does not follow a mount point, so it reads only the
/// volume its directory is on, and two directories of one drive letter mounted from two disks can be
/// walked at once. A walk's listings are bounded for each volume (<see cref="ListingBudget"/>), not for
/// each disk, so two walks on volumes of one disk would each list at full strength and make the disk
/// seek between them. Two drives tuned differently are of different kinds, so they are on different
/// disks and can be walked at once, but only where the disks are known: a tuner built without the
/// machine's devices knows none, and then every walk runs in turn, as every walk ran before.</para>
/// </summary>
internal static class DirectoryWalks
{
    /// <summary>
    /// The walks of the directories at <paramref name="directories"/> among <paramref name="asked"/>,
    /// one list for each set of disks. Asks each volume's device the first time it is met, so never call
    /// it on the UI thread.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DirectoryWalk>> Plan(
        IReadOnlyList<ScanRequest> asked, IReadOnlyList<int> directories, ScanTuner tuner)
    {
        if (directories.Count == 0)
        {
            return [];
        }

        return
        [
            .. FolderPipelines.ByDisks(directories, i => [asked[i].Path], tuner.StorageOf).Select(set => (IReadOnlyList<DirectoryWalk>)
            [
                .. set
                    .GroupBy(i => tuner.For(asked[i].Path).Walk)
                    .Select(walk => new DirectoryWalk(walk.Key, [.. walk])),
            ]),
        ];
    }

    /// <summary>
    /// Each set of <paramref name="sets"/> on a worker thread of its own, all at once, each running its
    /// walks one after another through <paramref name="walk"/>. One walk's failure stops the rest, and
    /// is what is thrown (<see cref="PipelinesAtOnce"/>).
    /// </summary>
    public static Task RunAsync(
        IReadOnlyList<IReadOnlyList<DirectoryWalk>> sets, Action<DirectoryWalk, CancellationToken> walk, CancellationToken ct) =>
        PipelinesAtOnce.RunAsync(
            sets,
            (set, stop) => WorkerThreads.Shared.RunAsync(
                () =>
                {
                    foreach (var each in set)
                    {
                        walk(each, stop);
                    }

                    return true;
                },
                stop),
            ct);
}
