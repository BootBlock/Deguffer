using System.Collections.Concurrent;
using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>
/// Reads every drive in two phases (§7.6): what is present, and then each drive's health, which can
/// wait on a disk spinning up. Each drive, the listing and the volumes have the same limit, so a
/// stalled disk delays only what is on it. A device call cannot be interrupted, so each runs through
/// a <see cref="SingleRun{T}"/>: a read that keeps being refreshed holds one thread for each drive,
/// one for the listing and one for the volumes, however often it is refreshed.
/// </summary>
public sealed class DriveHealthReader(IDriveProbe probe, IVolumeInventory inventory, IStorageQueries storage, TimeSpan answerLimit)
{
    /// <summary>Long enough for a disk to spin up from standby, and short enough that a stalled drive still gets a row.</summary>
    public static readonly TimeSpan DefaultAnswerLimit = TimeSpan.FromSeconds(30);

    /// <summary>Enough to keep a slow disk from delaying the rest, on a machine with more drives than that.</summary>
    private const int MostAtOnce = 4;

    private const string NotReported = "Windows does not report the health of drives connected this way, so only the details below are known.";
    private const string DidNotDescribe = "Windows could not describe this drive.";

    private static readonly IDriveHealthSource[] Sources = [new NvmeHealthSource(), new AtaHealthSource()];

    private readonly SingleRun<IReadOnlyList<DriveDescription>?> _listing = new();
    private readonly SingleRun<IReadOnlyDictionary<int, IReadOnlyList<LocalVolume>>> _volumes = new();
    private readonly ConcurrentDictionary<int, SingleRun<DriveHealth>> _drives = new();

    /// <summary>Every drive present, ordered by disk number, or null where Windows would not list them in time.</summary>
    public async Task<IReadOnlyList<DriveDescription>?> ListAsync(CancellationToken ct)
    {
        try
        {
            return await _listing.Join(() => Task.FromResult(List())).WaitAsync(answerLimit, ct);
        }
        catch (TimeoutException)
        {
            // A disk that will not describe itself holds the whole listing, and the caller says so.
            return null;
        }
    }

    /// <summary>
    /// Read each of <paramref name="drives"/>, reporting each as it finishes, and report what is
    /// stored on each. One drive failing or not answering is that drive's report and does not end
    /// the others.
    /// </summary>
    public Task ReadAsync(
        IReadOnlyList<DriveDescription> drives,
        IProgress<DriveHealth> health,
        IProgress<DriveVolumes> volumes,
        CancellationToken ct) =>
        Task.WhenAll(ReadDrivesAsync(drives, health, ct), ReportVolumesAsync(drives, volumes, ct));

    /// <summary>Every drive present, synchronously. Blocks on each drive's description.</summary>
    internal IReadOnlyList<DriveDescription>? List()
    {
        if (probe.PresentDisks() is not { } disks)
        {
            return null;
        }

        var described = disks.Select(Describe).ToList();

        // Some cheap drives and USB bridges report one serial for every unit, so a key shared by two
        // drives falls back to what tells them apart on this machine.
        var repeated = described.GroupBy(drive => drive.Key).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();

        return [.. described.Select(drive => repeated.Contains(drive.Key) ? drive with { Key = DriveKey.Of(drive.Disk) } : drive)];
    }

    private static DriveHealth Empty(DriveDescription drive, DriveVerdict verdict, string summary) =>
        new(drive.Key, drive.Name, verdict, summary, null, [], [], [], false);

    private static string Named(string model, int disk) => string.IsNullOrEmpty(model) ? $"Disk {disk}" : model;

    private static string VolumeText(LocalVolume volume)
    {
        var label = string.IsNullOrEmpty(volume.Label) ? volume.RootPath : $"{volume.RootPath} ({volume.Label})";

        return volume is { TotalBytes: { } total, FreeBytes: { } free }
            ? $"{label}, {FreeSpace.Format(free)} free of {FreeSpace.Format(total)}"
            : label;
    }

    private async Task ReadDrivesAsync(IReadOnlyList<DriveDescription> drives, IProgress<DriveHealth> progress, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = MostAtOnce, CancellationToken = ct };

        await Parallel.ForEachAsync(drives, options, async (drive, token) =>
        {
            DriveHealth health;

            try
            {
                var run = _drives.GetOrAdd(drive.Disk, _ => new SingleRun<DriveHealth>());

                health = await run.Join(() => Task.FromResult(Examine(drive))).WaitAsync(answerLimit, token);
            }
            catch (TimeoutException)
            {
                // The thread reading the drive carries on, and the next read joins it.
                health = Empty(drive, DriveVerdict.Unknown, $"The drive did not answer within {answerLimit.TotalSeconds:N0} seconds.");
            }

            progress.Report(health);
        });
    }

    private async Task ReportVolumesAsync(IReadOnlyList<DriveDescription> drives, IProgress<DriveVolumes> progress, CancellationToken ct)
    {
        IReadOnlyDictionary<int, IReadOnlyList<LocalVolume>> byDisk;

        try
        {
            byDisk = await _volumes.Join(VolumesByDiskAsync).WaitAsync(answerLimit, ct);
        }
        catch (TimeoutException)
        {
            // Volumes go on one line per drive and are optional, so a volume that will not answer
            // costs that line and no drive's reading.
            return;
        }

        foreach (var drive in drives)
        {
            var volumes = byDisk.GetValueOrDefault(drive.Disk, []);

            progress.Report(new DriveVolumes(drive.Key, string.Join("; ", volumes.Select(VolumeText)), [.. volumes.Select(volume => volume.RootPath)]));
        }
    }

    private DriveDescription Describe(int disk)
    {
        using var device = probe.Open(disk);

        return device.Ask(DriveQuestion.Device).Bytes is { } bytes && DeviceDescriptor.TryRead(bytes, out var descriptor)
            ? new DriveDescription(DriveKey.Of(descriptor.Model, descriptor.Serial, disk), disk, Named(descriptor.Model, disk)) { Descriptor = descriptor }
            : new DriveDescription(DriveKey.Of(disk), disk, Named(string.Empty, disk));
    }

    private DriveHealth Examine(DriveDescription drive)
    {
        if (drive.Descriptor is not { } descriptor)
        {
            return Empty(drive, DriveVerdict.Unknown, DidNotDescribe);
        }

        using var device = probe.Open(drive.Disk);
        var facts = DriveFacts.Of(descriptor, device);

        if (Array.Find(Sources, source => source.Reads(descriptor.Bus)) is not { } reader)
        {
            return Empty(drive, DriveVerdict.Unknown, NotReported) with { Facts = facts };
        }

        var readout = reader.Read(device, descriptor.Model);
        var verdict = DriveAssessment.Verdict(readout.Attributes, readout.HasPrimarySignal);

        return new DriveHealth(
            drive.Key,
            drive.Name,
            verdict,
            DriveAssessment.Summary(verdict),
            readout.TemperatureCelsius is { } celsius ? HealthText.Temperature(celsius) : null,
            DriveAssessment.Findings(readout.Attributes),
            [.. facts, .. readout.Facts],
            readout.Attributes,
            readout.MoreWhenElevated);
    }

    /// <summary>
    /// Each disk's volumes. The token is left out because a run outlives the read that
    /// started it, and the media cache is made for this run so a swapped disk is seen by the next.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<LocalVolume>>> VolumesByDiskAsync()
    {
        var mounted = await inventory.RefreshAsync(CancellationToken.None);
        var media = new VolumeMediaCache(storage);

        return mounted
            .Where(volume => volume.IsReady)
            .SelectMany(volume => media.Of(volume).PhysicalDisks.Select(disk => (Disk: disk, Volume: volume)))
            .GroupBy(entry => entry.Disk, entry => entry.Volume)
            .ToDictionary(group => group.Key, IReadOnlyList<LocalVolume> (group) => [.. group]);
    }
}
