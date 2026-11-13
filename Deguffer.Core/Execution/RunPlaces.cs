using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The paths a run's §5.6 verifications compare, each at every path it is reachable at, so a survivor is
/// measured against the run's reach as folders rather than as text. See <see cref="ReachedFolder"/>.
///
/// <para><b>A step and a protected path may name one folder two ways.</b> A step removing
/// <c>T:\run-1</c>, with <c>T:</c> substituted for the account's temporary folder, removes
/// <c>C:\Users\testuser\AppData\Local\Temp\run-1</c> and everything protected inside it. Compared as
/// text, nothing in the run held what went, so its loss read as something else on the machine removing
/// it, which is the §5.6 alarm suppressed by Deguffer's own removal.</para>
///
/// <para><b>Followed only where a question needs it, and once in the run.</b> Following a path asks
/// the machine where its volume is mounted, a run can target thousands of paths, and a plan whose
/// protected paths all survive untouched asks nothing of the run's reach at all (G4). One set serves
/// every verification of the run (<see cref="RunReach.PlacesOn"/>), so a target the first plan's
/// verification followed is not followed again for the next. Where each drive is mounted is asked
/// once for the run too, since nearly every path shares a drive
/// (<see cref="RememberedMountPoints"/>).</para>
///
/// <para><b>Safe to ask from more than one thread.</b> The planner verifies one plan after another,
/// but the reach is handed to every provider, and nothing stops a caller verifying two at once. Two
/// threads asking about one path at once may each follow it, and both answers are the same folder.</para>
/// </summary>
/// <param name="volumes">Asked every other path each folder is reachable at.</param>
internal sealed class RunPlaces(RunReach reach, IVolumeInventory volumes)
{
    private readonly ConcurrentDictionary<string, ReachedFolder> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly RememberedMountPoints _volumes = new(volumes);
    private IReadOnlyList<(string Path, ReachedFolder Folder)>? _targets;
    private IReadOnlyList<ReachedFolder>? _probed;

    /// <summary>The run's reach this compares against.</summary>
    public RunReach Reach => reach;

    /// <summary>Every path the run will destroy outright, each as it was named and as a folder.</summary>
    public IReadOnlyList<(string Path, ReachedFolder Folder)> Targets =>
        _targets ??= [.. reach.TargetedPaths.Select(target => (target, At(target)))];

    /// <summary>Every path a tool's own command in the run is sent to clear, as a folder.</summary>
    public IReadOnlyList<ReachedFolder> Probed => _probed ??= [.. reach.ProbedPaths.Select(At)];

    /// <summary>The folder at <paramref name="path"/>, followed the first time it is asked about.</summary>
    public ReachedFolder At(string path) =>
        _folders.TryGetValue(path, out var folder) ? folder : _folders.GetOrAdd(path, ReachedFolder.At(path, _volumes));
}
