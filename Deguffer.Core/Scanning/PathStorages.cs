using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What each path of one measurement is on, with each drive letter asked about once for the whole
/// measurement rather than once for every path (<see cref="RememberedSubstitutes"/>, #1065).
///
/// <para>A measurement asks what its paths are on to put them into pipelines by disk, to register
/// its walks with the index builds that yield to them, and to choose each walk's values. Every one of
/// those questions goes through the one instance the measurement made at its start, so a few thousand
/// paths on one drive ask that drive's letter once. A volume's kind is already remembered for the pass
/// (<see cref="VolumeMediaCache"/>), and which volume holds a path is read from the remembered list,
/// so the letter is the one question about a path that reaches the machine.</para>
///
/// <para>Safe to ask from several threads at once: the pipelines of one measurement run at the same
/// time, and an index build asks about the walks it may yield to from its own thread.</para>
/// </summary>
internal sealed class PathStorages
{
    private readonly ScanTuner _tuner;
    private readonly IVolumeInventory? _volumes;

    /// <param name="volumes">The volumes asked through, or null where the tuner knows none.</param>
    internal PathStorages(ScanTuner tuner, IVolumeInventory? volumes)
    {
        _tuner = tuner;
        _volumes = volumes;
    }

    /// <summary>What <see cref="ScanTuner.PathStorageOf"/> answers for <paramref name="path"/>.</summary>
    public PathStorage? PlaceOf(string path) => _tuner.PathStorageThrough(_volumes, path);

    /// <summary>What <see cref="ScanTuner.StorageOf"/> answers for <paramref name="path"/>.</summary>
    public VolumeMedia? Of(string path) => PlaceOf(path)?.Media;

    /// <summary>What <see cref="ScanTuner.For(string)"/> answers for <paramref name="path"/>.</summary>
    public VolumeTuning For(string path) => _tuner.For(Of(path));
}
