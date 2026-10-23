using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What a scan starting now runs with: the route the user allows, and the values for the kind of
/// drive the scan reads.
///
/// <para><b>Read when a scan starts, never kept.</b> The settings are read through
/// <see cref="ICurrentPreferences"/> at each call, so a change on the Settings page takes effect from
/// the next scan. The drive's kind is asked of <see cref="VolumeMediaCache"/>, which remembers it
/// until <see cref="Invalidate"/>, because asking opens a device.</para>
/// </summary>
public sealed class ScanTuner
{
    private readonly ICurrentPreferences _preferences;
    private readonly VolumeMediaCache? _media;
    private readonly IVolumeInventory? _volumes;

    public ScanTuner(ICurrentPreferences preferences, VolumeMediaCache media, IVolumeInventory volumes)
    {
        _preferences = preferences;
        _media = media;
        _volumes = volumes;
    }

    /// <summary>
    /// <paramref name="preferences"/>, with every drive of unknown kind, so no device is opened to
    /// learn what it is. The benchmark builds one to walk with the values it is given.
    /// </summary>
    internal ScanTuner(ICurrentPreferences preferences) => _preferences = preferences;

    /// <summary>
    /// The shipped settings, with every drive of unknown kind, so every scan runs with the values
    /// scans used before they could be set. For a scanner built outside the app, a test included,
    /// which must not open the machine's devices to learn what they are.
    /// </summary>
    public static ScanTuner Shipped { get; } = new(DefaultPreferences.Instance);

    /// <summary>The route the user allows.</summary>
    public ScanRoute Route => _preferences.Current.Scanning.Route;

    /// <summary>Whether the user asked for the walk even where the file table could be read.</summary>
    public bool WalkOnly => Route is ScanRoute.WalkOnly;

    /// <summary>
    /// The values a scan of <paramref name="path"/> runs with. Asks the device the first time a
    /// volume is met, so never call it on the UI thread.
    /// </summary>
    public VolumeTuning For(string path) => VolumeTuning.Resolve(_preferences.Current.Scanning, MediaOf(path));

    /// <summary>The values a read of the file table on <paramref name="driveLetter"/> runs with.</summary>
    public VolumeTuning ForVolume(char driveLetter) => For($@"{char.ToUpperInvariant(driveLetter)}:\");

    /// <summary>
    /// The kind of storage <paramref name="path"/> is on. A share is <see cref="StorageMedia.Network"/>
    /// without asking anything. A path on no volume the inventory lists is
    /// <see cref="StorageMedia.Unknown"/>, which gets the conservative values.
    /// </summary>
    public StorageMedia MediaOf(string path) => StorageOf(path)?.Class ?? StorageMedia.Unknown;

    /// <summary>
    /// What <paramref name="path"/> is on, its disks included, or null where nothing was asked: a
    /// tuner built without the machine's devices, or a path on no volume the inventory lists. A share
    /// is <see cref="StorageMedia.Network"/> on no disk of this machine, without asking anything. Asks
    /// the device the first time a volume is met, so never call it on the UI thread.
    /// </summary>
    public VolumeMedia? StorageOf(string path)
    {
        if (_media is null || _volumes is null)
        {
            return null;
        }

        if (LongPath.IsShare(path))
        {
            return new VolumeMedia(StorageMedia.Network, []);
        }

        return HostVolume.For(_volumes, path) is { } volume ? _media.Of(volume) : null;
    }

    /// <summary>
    /// Each drive with a letter that is ready now, and its kind, in order of letter. For the Settings
    /// page, which says what Auto chose for the drives present. Asks each device the first time, so
    /// never call it on the UI thread.
    ///
    /// <para>A mapped share is listed without being ready: the inventory does not ask whether one
    /// is (<see cref="VolumeReadiness.NotAsked"/>), and its kind is network whether or not the server
    /// answers, so what Auto chose for it is true either way.</para>
    /// </summary>
    public IReadOnlyList<DriveKind> Drives(CancellationToken ct)
    {
        if (_media is null || _volumes is null)
        {
            return [];
        }

        var drives = new List<DriveKind>();

        foreach (var volume in _volumes.Volumes
            .Where(volume => volume is { IsReady: true } or { Kind: DriveType.Network } && VolumeRoot.IsDriveTop(volume.RootPath))
            .OrderBy(volume => volume.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            // Between drives, because each first question about one waits on its device.
            ct.ThrowIfCancellationRequested();
            drives.Add(new DriveKind(volume.RootPath[..2], _media.Of(volume).Class));
        }

        return drives;
    }

    /// <summary>Forget each drive's kind, so the next scan sees disks attached or swapped since.</summary>
    public void Invalidate() => _media?.Invalidate();
}
