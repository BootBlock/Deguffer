using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Which steps of one plan may be under way at the same moment. A row of build output is one plan with
/// a step for each folder, and a small folder's removal is mostly fixed costs that leave the disk idle:
/// the use check, the standing-folder check, the probes and the walk's first listings. Steps that run
/// together overlap those costs.
///
/// <para><b>Only removals Deguffer makes itself, and only of folders apart.</b> A directory, a clear
/// and a file step may run beside each other where no folder one destroys holds, or is inside, a
/// folder the other destroys. Two removals in one tree would each take what the other was trying, and
/// what each recorded as left standing would be the other's doing. Every other step runs alone: a
/// tool's command, the Recycle Bin, Disk Cleanup, local copies, restore points and profiles act
/// through something Deguffer does not walk, so nothing here can say where they reach. So does a
/// removal whose use check first asks a program to let go, because the request is made once for every
/// step sharing the check (see <see cref="ReleasingProcessCheck.ReleaseAsync"/>), and a step that ran
/// beside the one making it would be held back by a program that was about to stop.</para>
///
/// <para><b>Compared as folders.</b> Each path is taken at every path it is reachable at, as
/// <see cref="RunPlaces"/> takes the run's, and also where Windows says it leads once every link on
/// the way is followed: <c>C:\src\app\obj</c> and a junction <c>C:\work</c> to <c>C:\src</c> make
/// <c>C:\work\app\obj</c> the same folder. Following only adds places, so a link can keep two steps
/// apart and never put them together. A path on a share or a network drive is not followed, for the
/// reasons <see cref="HostVolume.Unopened"/> gives, and nothing can say which other path is the same
/// folder, so its step runs alone.</para>
///
/// <para><b>Every step's folders are found before the first step starts.</b> Following a path opens
/// a handle on it, and a handle opened on a later step's folder while an earlier step removed the
/// folder holding it could stand in that removal's way. Each step is followed once, and a step that
/// runs alone is not followed at all.</para>
/// </summary>
internal sealed class StepOverlap
{
    // Empty for a step whose folders nothing can tell apart from another's, which runs alone.
    private readonly IReadOnlyList<ReachedFolder>[] _folders;

    private StepOverlap(IReadOnlyList<ReachedFolder>[] folders) => _folders = folders;

    /// <summary>Which of <paramref name="steps"/> may run together, found before any of them runs.</summary>
    /// <param name="volumes">Asked every other path each folder is reachable at.</param>
    /// <param name="fileSystem">Asked where each path leads once its links are followed.</param>
    public static StepOverlap Of(IReadOnlyList<CleanupStep> steps, IVolumeInventory volumes, IFileSystem fileSystem)
    {
        var remembered = new RememberedMountPoints(volumes);
        var places = new FinalPlaces(fileSystem, remembered);

        return new StepOverlap([.. steps.Select(step => FoldersOf(step, remembered, places))]);
    }

    /// <summary>Whether the step at <paramref name="step"/> may start while every step in <paramref name="underway"/> runs.</summary>
    public bool MayStartBeside(int step, IReadOnlyCollection<int> underway) =>
        underway.Count == 0 || (_folders[step] is { Count: > 0 } own && underway.All(other => Apart(own, _folders[other])));

    private static bool Apart(IReadOnlyList<ReachedFolder> one, IReadOnlyList<ReachedFolder> other) =>
        other.Count > 0 && one.All(folder => other.All(theirs => !folder.Holds(theirs) && !theirs.Holds(folder)));

    private static IReadOnlyList<ReachedFolder> FoldersOf(CleanupStep step, IVolumeInventory volumes, FinalPlaces places)
    {
        if (step is not (DeleteDirectoryStep or ClearDirectoryStep or DeleteFileStep) || step is DeleteStep { UseCheck: ReleasingProcessCheck })
        {
            return [];
        }

        var destroys = ((DeleteStep)step).Destroys;

        if (destroys.Any(path => HostVolume.Unopened(volumes, path) is UnopenedPlace.Share))
        {
            return [];
        }

        return [.. destroys.Select(places.Rule)];
    }
}
