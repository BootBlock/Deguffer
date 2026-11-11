namespace Deguffer.Core.Health;

/// <summary>
/// The latest reading of each drive and the volumes on it, as Health last read them, so that the
/// places that list volumes can name a drive of concern without asking any drive anything (§7.6).
/// Until Health has read, it knows nothing and every volume's answer is null.
///
/// <para>Written by the Health page as each report arrives and read by the pages that list volumes,
/// all on the UI thread, so it takes no lock and raises <see cref="Changed"/> on that thread.</para>
/// </summary>
public sealed class HealthByVolume
{
    private readonly Dictionary<DriveKey, DriveHealth> _reports = [];
    private readonly Dictionary<DriveKey, IReadOnlyList<string>> _volumes = [];

    /// <summary>Raised when what <see cref="Of"/> answers for some volume may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Forget every drive a finished listing no longer has. A drive taken away no longer speaks for
    /// the volumes it held.
    /// </summary>
    public void Listed(IReadOnlyList<DriveDescription> drives)
    {
        var present = drives.Select(drive => drive.Key).ToHashSet();
        var gone = _reports.Keys.Concat(_volumes.Keys).Where(key => !present.Contains(key)).Distinct().ToList();

        foreach (var key in gone)
        {
            _reports.Remove(key);
            _volumes.Remove(key);
        }

        if (gone.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Take a drive's latest reading. A note says only its name and verdict, so a reading that changes neither raises nothing.</summary>
    public void Record(DriveHealth report)
    {
        var said = _reports.TryGetValue(report.Key, out var last) && last.Verdict == report.Verdict && last.Name == report.Name;

        _reports[report.Key] = report;

        if (!said)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Take where a drive's volumes are mounted, in place of where they were, so a volume moved to
    /// another drive is answered for by that drive alone.
    /// </summary>
    public void Record(DriveVolumes volumes)
    {
        if (_volumes.TryGetValue(volumes.Key, out var last) && last.SequenceEqual(volumes.RootPaths, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _volumes[volumes.Key] = volumes.RootPaths;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What to say about the drives under the volume mounted at <paramref name="rootPath"/>, any of
    /// the places it is mounted, or null
    /// where none read so far is a concern. A volume that spans several drives is answered for by
    /// the worst of them.
    /// </summary>
    public VolumeHealthNote? Of(string rootPath) =>
        VolumeHealthNote.For(
            _volumes
                .Where(drive => drive.Value.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
                .Select(drive => _reports.GetValueOrDefault(drive.Key))
                .OfType<DriveHealth>());
}
