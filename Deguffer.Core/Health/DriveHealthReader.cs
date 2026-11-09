using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>
/// Reads every drive in two phases (§7.6): what is present, which the driver answers at once, and
/// then each drive's health, which can wait on a disk spinning up.
///
/// <para>A device call cannot be interrupted. One thread stays with a drive that does not answer
/// until the drive does, so a drive still being asked is joined by the next read rather than asked
/// again, and a read that keeps being refreshed holds one thread per drive at most.</para>
/// </summary>
public sealed class DriveHealthReader(IDriveProbe probe, IVolumeInventory inventory, IStorageQueries storage, TimeSpan answerLimit)
{
    /// <summary>How long a drive gets, which is more than a disk needs to spin up from standby.</summary>
    public static readonly TimeSpan DefaultAnswerLimit = TimeSpan.FromSeconds(30);

    /// <summary>Enough to keep a slow disk from delaying the rest, on a machine with more drives than that.</summary>
    private const int MostAtOnce = 4;

    private const string NotReported = "Windows does not report the health of drives connected this way, so only the details below are known.";
    private const string DidNotDescribe = "Windows could not describe this drive.";

    private static readonly IDriveHealthSource[] Sources = [new NvmeHealthSource(), new AtaHealthSource()];

    private readonly Dictionary<DriveKey, Task<DriveHealth>> _asking = [];
    private readonly Lock _gate = new();

    /// <summary>Every drive present, ordered by disk number, or null where Windows would not list them. Blocks briefly.</summary>
    public IReadOnlyList<DriveDescription>? List()
    {
        if (probe.PresentDisks() is not { } disks)
        {
            return null;
        }

        return [.. disks.Select(Describe)];
    }

    /// <summary>
    /// Read each of <paramref name="drives"/>, reporting each as it finishes. One drive failing or
    /// not answering is that drive's report and does not end the others.
    /// </summary>
    public async Task ReadAsync(IReadOnlyList<DriveDescription> drives, IProgress<DriveHealth> progress, CancellationToken ct)
    {
        var volumes = VolumesByDisk(await inventory.RefreshAsync(ct));
        var options = new ParallelOptions { MaxDegreeOfParallelism = MostAtOnce, CancellationToken = ct };

        await Parallel.ForEachAsync(drives, options, async (drive, token) =>
        {
            DriveHealth health;

            try
            {
                health = await Asking(drive, volumes).WaitAsync(answerLimit, token);
            }
            catch (TimeoutException)
            {
                // The thread reading the drive carries on, and the next read joins it.
                health = Empty(drive, DriveVerdict.Unknown, $"The drive did not answer within {answerLimit.TotalSeconds:N0} seconds.");
            }

            progress.Report(health);
        });
    }

    private static DriveHealth Empty(DriveDescription drive, DriveVerdict verdict, string summary) =>
        new(drive.Key, drive.Disk, drive.Name, verdict, summary, null, [], [], [], false, null);

    private DriveDescription Describe(int disk)
    {
        using var device = probe.Open(disk);

        return device.Ask(DriveQuestion.Device).Bytes is { } bytes && DeviceDescriptor.TryRead(bytes, out var descriptor)
            ? new DriveDescription(DriveKey.Of(descriptor.Model, descriptor.Serial, disk), disk, Named(descriptor.Model, disk)) { Descriptor = descriptor }
            : new DriveDescription(DriveKey.Of(disk), disk, Named(string.Empty, disk));
    }

    private static string Named(string model, int disk) => string.IsNullOrEmpty(model) ? $"Disk {disk}" : model;

    private Task<DriveHealth> Asking(DriveDescription drive, IReadOnlyDictionary<int, string> volumes)
    {
        lock (_gate)
        {
            if (_asking.TryGetValue(drive.Key, out var running))
            {
                return running;
            }

            var task = Task.Run(() => Examine(drive, volumes));
            _asking[drive.Key] = task;

            task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _asking.Remove(drive.Key);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return task;
        }
    }

    private DriveHealth Examine(DriveDescription drive, IReadOnlyDictionary<int, string> volumes)
    {
        if (drive.Descriptor is not { } descriptor)
        {
            return Empty(drive, DriveVerdict.Unknown, DidNotDescribe);
        }

        using var device = probe.Open(drive.Disk);
        var facts = DriveFacts.Of(descriptor, device);

        if (Array.Find(Sources, source => source.Reads(descriptor.Bus)) is not { } reader)
        {
            return Empty(drive, DriveVerdict.Unknown, NotReported) with { Facts = facts, Volumes = volumes.GetValueOrDefault(drive.Disk) };
        }

        var readout = reader.Read(device, descriptor.Model);
        var verdict = DriveAssessment.Verdict(readout.Attributes, readout.HasPrimarySignal);

        return new DriveHealth(
            drive.Key,
            drive.Disk,
            drive.Name,
            verdict,
            DriveAssessment.Summary(verdict),
            readout.TemperatureCelsius,
            DriveAssessment.Findings(readout.Attributes),
            [.. facts, .. readout.Facts],
            readout.Attributes,
            readout.MoreWhenElevated,
            volumes.GetValueOrDefault(drive.Disk));
    }

    /// <summary>A line of each disk's volumes. The media cache is made for this read, so a swapped disk is seen by the next.</summary>
    private Dictionary<int, string> VolumesByDisk(IReadOnlyList<LocalVolume> mounted)
    {
        var media = new VolumeMediaCache(storage);

        return mounted
            .Where(volume => volume.IsReady)
            .SelectMany(volume => media.Of(volume).PhysicalDisks.Select(disk => (Disk: disk, Volume: volume)))
            .GroupBy(entry => entry.Disk, entry => entry.Volume)
            .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(VolumeText)));
    }

    private static string VolumeText(LocalVolume volume)
    {
        var label = string.IsNullOrEmpty(volume.Label) ? volume.RootPath : $"{volume.RootPath} ({volume.Label})";

        return volume is { TotalBytes: { } total, FreeBytes: { } free }
            ? $"{label}, {Scanning.FreeSpace.Format(free)} free of {Scanning.FreeSpace.Format(total)}"
            : label;
    }
}
