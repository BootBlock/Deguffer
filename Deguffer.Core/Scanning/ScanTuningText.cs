using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The words the Settings page uses for a kind of drive and for what a scan of a drive runs with.
/// </summary>
public static class ScanTuningText
{
    /// <summary>A kind of storage, as a person names it.</summary>
    public static string MediaName(StorageMedia media) => media switch
    {
        StorageMedia.Nvme => "NVMe SSD",
        StorageMedia.SolidState => "Other SSD",
        StorageMedia.Rotational => "Spinning disk",
        StorageMedia.Removable => "Removable or USB",
        StorageMedia.Network => "Network",
        StorageMedia.Virtual => "Virtual disk",
        StorageMedia.Unknown => "Unknown kind",
        _ => throw new ArgumentOutOfRangeException(nameof(media), media, null),
    };

    /// <summary>
    /// What a scan of <paramref name="drive"/> runs with under <paramref name="preferences"/>, each
    /// value marked where Auto chose it, such as
    /// <c>C: (NVMe SSD): 16 threads (Auto), 16 KiB listing buffer (Auto), 16,384 KiB table reads (Auto)</c>.
    /// </summary>
    public static string Describe(DriveKind drive, ScanPreferences preferences)
    {
        var tuning = VolumeTuning.Resolve(preferences, drive.Media);
        var chosen = tuning.Chosen;

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{drive.Drive} ({MediaName(tuning.Media)}): " +
            $"{tuning.Walk.Threads:N0} threads{AutoMark(chosen.WalkThreads)}, " +
            $"{tuning.Walk.ListingBufferBytes / 1024:N0} KiB listing buffer{AutoMark(chosen.ListingBufferKiB)}, " +
            $"{tuning.Table.ReadBytes / 1024:N0} KiB table reads{AutoMark(chosen.TableReadKiB)}");
    }

    private static string AutoMark(int? chosen) => chosen is null ? " (Auto)" : string.Empty;
}
