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
    /// </summary>
    public FakeVolumeInventory With(string rootPath, DriveType kind = DriveType.Fixed, bool isReady = true)
    {
        _volumes.Add(new LocalVolume(rootPath, kind, isReady));
        return this;
    }

    public void Invalidate() => InvalidateCount++;
}
