namespace Deguffer.Core.Exploring.Hidden;

/// <summary>
/// The machine's <see cref="IHiddenSpaceSource"/>: the shadow copy storage from the Volume Shadow
/// Copy service, and the storage reserve from the file system.
/// </summary>
public sealed class WindowsHiddenSpace : IHiddenSpaceSource
{
    public static WindowsHiddenSpace Default { get; } = new();

    private WindowsHiddenSpace()
    {
    }

    /// <summary>
    /// Off the caller's thread, because the shadow copy storage is asked of a service in another
    /// process, and the page that asks is waiting to draw.
    /// </summary>
    public Task<HiddenSpace> ReadAsync(string volumeRoot, CancellationToken ct) =>
        Task.Run(
            () =>
            {
                var root = Path.EndsInDirectorySeparator(volumeRoot) ? volumeRoot : volumeRoot + Path.DirectorySeparatorChar;
                var reserved = DiskSpaceCalls.ReserveOn(root);

                ct.ThrowIfCancellationRequested();

                return new HiddenSpace(ShadowStorageCalls.On(root), reserved);
            },
            ct);
}
