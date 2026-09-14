using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Volumes rooted in a temp directory, so a per-volume provider is asserted against drives we build
/// rather than against whichever ones the developer happens to have mounted.
/// </summary>
public sealed class FakeVolumeInventory : IVolumeInventory
{
    private readonly List<LocalVolume> _volumes = [];

    public IReadOnlyList<LocalVolume> Volumes => _volumes;

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Pretend <paramref name="rootPath"/> is a mounted volume. It defaults to the fixed, ready
    /// case, so a test that names another kind is visibly testing that kind.
    ///
    /// <para>Says nothing about the label or the space a volume reports. Those are read inside
    /// <see cref="VolumeInventory"/> from a real <c>DriveInfo</c>, and no rule that decides a
    /// deletion consults them, so there is nothing here for a fake to stand in for yet.</para>
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
    public FakeVolumeInventory With(
        string rootPath,
        DriveType kind = DriveType.Fixed,
        bool isReady = true,
        VolumeFeatures features = VolumeFeatures.ReparsePoints,
        IReadOnlyList<string>? alsoMountedAt = null)
    {
        _volumes.Add(new LocalVolume(rootPath, kind, isReady, Features: features, AlsoMountedAt: alsoMountedAt));

        return this;
    }

    public void Invalidate() => InvalidateCount++;
}
