using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Volumes rooted in a temp directory, so a per-volume provider is asserted against drives we build
/// rather than against whichever ones the developer happens to have mounted.
/// </summary>
public sealed class FakeVolumeInventory : IVolumeInventory
{
    private readonly List<LocalVolume> _volumes = [];

    private string? _answer;

    public IReadOnlyList<LocalVolume> Volumes => _volumes;

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Pretend <paramref name="rootPath"/> is a mounted volume. It defaults to the fixed, ready
    /// case, so a test that names another kind is visibly testing that kind.
    ///
    /// <para>Says nothing about the label a volume reports. It is read inside
    /// <see cref="VolumeInventory"/> from a real <c>DriveInfo</c>, and no rule consults it. The
    /// capacity and the free space are the exception, because Explore draws them beside a scan of the
    /// whole volume.</para>
    /// </summary>
    /// <param name="features">
    /// What the volume says it supports. Defaults to the local NTFS reading, because a fake volume
    /// is a directory on the machine running the suite and that is what it really is — so a test
    /// naming <see cref="VolumeFeatures.RemoteStorage"/> is visibly testing a cloud mount.
    /// </param>
    /// <param name="alsoMountedAt">
    /// Every other path the volume is reachable at, each ending in a separator as Windows reports
    /// them. A test that names one is testing a volume mounted in more than one place.
    /// </param>
    /// <param name="totalBytes">The volume's capacity, or null where it would not say.</param>
    /// <param name="freeBytes">What the volume says it has left, or null where it would not say.</param>
    public FakeVolumeInventory With(
        string rootPath,
        DriveType kind = DriveType.Fixed,
        bool isReady = true,
        VolumeFeatures features = VolumeFeatures.ReparsePoints,
        IReadOnlyList<string>? alsoMountedAt = null,
        long? totalBytes = null,
        long? freeBytes = null)
    {
        _volumes.Add(new LocalVolume(
            rootPath,
            kind,
            isReady,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes,
            Features: features,
            AlsoMountedAt: alsoMountedAt));

        return this;
    }

    /// <summary>
    /// Pretend the volume at <paramref name="rootPath"/> has been taken away. With
    /// <see cref="With"/> after it, the same volume read again with other figures.
    /// </summary>
    public FakeVolumeInventory Without(string rootPath)
    {
        _volumes.RemoveAll(volume => volume.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase));

        return this;
    }

    /// <summary>
    /// Every path <see cref="MountPointOf"/> was asked about, in the form it arrived in, so a test
    /// can see that a rule asked the machine rather than reading a drive letter.
    /// </summary>
    public List<string> MountPointQueries { get; } = [];

    /// <summary>
    /// The longest mount point of any volume here that holds <paramref name="path"/>, which is how
    /// Windows answers for one volume mounted inside another, or null where none does — the answer
    /// the machine gives for a drive that is not there.
    ///
    /// <para>Answers from the volumes as they are at the call rather than as they were when the list
    /// was last read, because the real one asks the machine each time: a volume added after a caller
    /// was built is seen by it, which is what a test of that behaviour needs.</para>
    /// </summary>
    public string? MountPointOf(string path)
    {
        MountPointQueries.Add(path);

        if (_answer is { } answer)
        {
            return answer;
        }

        var comparable = LongPath.Display(path);

        return _volumes
            .SelectMany(volume => volume.MountPoints)
            .Where(mountPoint => HostVolume.Holds(mountPoint, comparable))
            .MaxBy(mountPoint => mountPoint.Length);
    }

    /// <summary>
    /// Answer every <see cref="MountPointOf"/> with <paramref name="mountPoint"/>, whatever the path.
    /// Windows gives an answer that is not a prefix of the path for a path through a junction to
    /// another volume: it names the volume on the junction's far side.
    /// </summary>
    public FakeVolumeInventory Answering(string mountPoint)
    {
        _answer = mountPoint;

        return this;
    }

    public void Invalidate() => InvalidateCount++;
}
