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
    /// case with nothing known about its label or its space, so a test that names any of those is
    /// visibly testing them.
    /// </summary>
    public FakeVolumeInventory With(
        string rootPath,
        DriveType kind = DriveType.Fixed,
        bool isReady = true,
        string? label = null,
        long? totalBytes = null,
        long? freeBytes = null)
    {
        _volumes.Add(new LocalVolume(rootPath, kind, isReady, label, totalBytes, freeBytes));
        return this;
    }

    public void Invalidate() => InvalidateCount++;
}
