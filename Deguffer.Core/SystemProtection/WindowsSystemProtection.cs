using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;

namespace Deguffer.Core.SystemProtection;

/// <summary>The machine's <see cref="ISystemProtection"/>.</summary>
public sealed class WindowsSystemProtection : ISystemProtection
{
    public static WindowsSystemProtection Default { get; } = new(SystemDirectories.Current, VolumeInventory.Current);

    private readonly IVolumeInventory _volumes;

    /// <summary>
    /// System Restore's library in Windows' own folder, never found by the search order, because the
    /// one function taken from it removes restore points. A 32-bit process is redirected to the 32-bit
    /// copy, which is the one it can load.
    /// </summary>
    private readonly string _library;

    private WindowsSystemProtection(ISystemDirectories system, IVolumeInventory volumes)
    {
        _volumes = volumes;
        _library = Path.Combine(system.WindowsDirectory, "System32", "srclient.dll");
    }

    public RestorePointListing ListRestorePoints() => RestorePointCalls.List();

    public RemovalAnswer RemoveRestorePoint(uint sequenceNumber) => RestorePointCalls.Remove(_library, sequenceNumber);

    public ShadowCopyListing ListShadowCopies() => ShadowCopyCalls.List();

    /// <summary>
    /// Every ready fixed volume. Shadow copy storage can sit on a different volume from the one it
    /// serves, so asking only the system drive would miss storage System Protection keeps elsewhere.
    /// </summary>
    public IReadOnlyList<VolumeShadowStorage> ReadStorage() =>
    [
        .. _volumes.Volumes
            .Where(volume => volume is { IsReady: true, Kind: DriveType.Fixed })
            .Select(volume => Path.EndsInDirectorySeparator(volume.RootPath) ? volume.RootPath : volume.RootPath + Path.DirectorySeparatorChar)
            .Select(root => new VolumeShadowStorage(root, ShadowStorageCalls.On(root))),
    ];
}
