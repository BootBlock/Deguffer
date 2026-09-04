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
    public FakeVolumeInventory With(string rootPath, DriveType kind = DriveType.Fixed, bool isReady = true)
    {
        _volumes.Add(new LocalVolume(rootPath, kind, isReady));
        return this;
    }

    public void Invalidate() => InvalidateCount++;
}
