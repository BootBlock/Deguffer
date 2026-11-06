using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page reads about the volume one scan is taken of, started beside the scan so
/// that none of it is waited for between the end of the scan and the map (#485).
///
/// <para><b>Started as the scan starts.</b> The machine's volumes are read again, which asks every
/// volume it has mounted, an empty optical drive or a sleeping disk included. Where the scan covers
/// the whole of a volume, what Windows states about its hidden space is asked for too, which waits on
/// the Volume Shadow Copy service and on that service starting where it had stopped. Neither depends
/// on what the scan finds, and both take long enough to be worth running while it does.</para>
///
/// <para><b>Finished with the tree.</b> Only whether the scan counted <c>System Volume Information</c>
/// needs it, and only the free space is read again as the scan ends, through
/// <see cref="VolumeSpace.Remeasured"/>, so the figure still describes the disk as the scan found it.
/// The shadow copy storage and the reserve rarely move during one scan.</para>
/// </summary>
public sealed class ScannedVolume
{
    private readonly IVolumeInventory _volumes;

    private readonly string _scannedRoot;

    private readonly Task<(VolumeSpace Space, HiddenSpace Hidden)> _read;

    private readonly Task<IReadOnlyList<LocalVolume>> _refreshed;

    private ScannedVolume(
        IVolumeInventory volumes,
        string scannedRoot,
        Task<(VolumeSpace Space, HiddenSpace Hidden)> read,
        Task<IReadOnlyList<LocalVolume>> refreshed)
    {
        _volumes = volumes;
        _scannedRoot = scannedRoot;
        _read = read;
        _refreshed = refreshed;
    }

    /// <summary>
    /// Start reading the volume <paramref name="scannedRoot"/> is on, as its scan starts.
    ///
    /// <para>Whether the scan covers the whole of a volume is asked of the list the inventory
    /// remembers, which the refresh started here replaces only once it has finished, so the answer
    /// does not wait for it. It is asked off the caller's thread all the same: where a planning pass
    /// has dropped the list (<see cref="IVolumeInventory.Invalidate"/>), reading it asks every volume
    /// the machine has mounted, and the caller is the window (#181). Nothing is asked of Windows about
    /// hidden space where the scan covers a folder, because nothing is drawn beside one.</para>
    ///
    /// <para>Neither reading is awaited by the refusal set. <see cref="ProtectionScope.Scanned"/> reads
    /// the mount topology from the same inventory, which answers from the remembered list until the
    /// refresh lands, and a removal checks again immediately before anything is removed (§7.1).</para>
    /// </summary>
    /// <param name="ct">The scan's cancellation, which stops both readings with it.</param>
    public static ScannedVolume Start(
        IVolumeInventory volumes,
        IHiddenSpaceSource hidden,
        string scannedRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hidden);

        return new ScannedVolume(
            volumes,
            scannedRoot,
            ReadAsync(volumes, hidden, scannedRoot, ct),
            volumes.RefreshAsync(ct));
    }

    private static async Task<(VolumeSpace Space, HiddenSpace Hidden)> ReadAsync(
        IVolumeInventory volumes,
        IHiddenSpaceSource hidden,
        string scannedRoot,
        CancellationToken ct)
    {
        var space = await Task.Run(() => VolumeSpace.Of(volumes, scannedRoot), ct).ConfigureAwait(false);

        return space == VolumeSpace.None
            ? (space, HiddenSpace.None)
            : (space, await hidden.ReadAsync(LongPath.Display(scannedRoot), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The space to draw beside the finished scan <paramref name="tree"/>, or
    /// <see cref="VolumeSpace.None"/> where it covered a folder, or where the volume no longer states
    /// both figures.
    ///
    /// <para>Waits for what Windows states about the hidden space, which was asked for as the scan
    /// started, and never for the list of volumes.</para>
    /// </summary>
    public async Task<VolumeSpace> SpaceAsync(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var (space, hidden) = await _read.ConfigureAwait(false);

        return space.Remeasured(_volumes, _scannedRoot) is var now && now == VolumeSpace.None
            ? VolumeSpace.None
            : now with { Hidden = hidden, CountedSystemVolumeInformation = VolumeSpace.Counted(tree) };
    }

    /// <summary>
    /// The <c>\\?\Volume{GUID}\</c> name of the volume the scan was taken of, from the list read
    /// again as the scan started, or null where no volume in it holds the scanned root.
    ///
    /// <para>Read from the fresh list because a drive letter can have moved to another disk since the
    /// remembered list was read, and the name is what keeps a scan of one disk from being compared
    /// with a scan of the other.</para>
    /// </summary>
    public async Task<string?> NameAsync()
    {
        try
        {
            await _refreshed.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The scan's cancellation stops the refresh, and a cancel that arrives after the scan has
            // finished must not cost it its record. The inventory keeps the list it had, which is the
            // one the scan was allowed to start from.
        }

        return HostVolume.For(_volumes, _scannedRoot)?.VolumeName;
    }
}
