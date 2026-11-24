using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Text;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The words the Settings page uses for a kind of drive and for what a scan of a drive runs with.
/// </summary>
public static class ScanTuningText
{
    /// <summary>The kinds of drive in the order the kind picker lists them, the common ones first.</summary>
    public static IReadOnlyList<StorageMedia> Kinds => KindOrder;

    private static readonly StorageMedia[] KindOrder =
    [
        StorageMedia.Nvme,
        StorageMedia.SolidState,
        StorageMedia.Rotational,
        StorageMedia.Removable,
        StorageMedia.Network,
        StorageMedia.Virtual,
        StorageMedia.Unknown,
    ];

    /// <summary>Where <paramref name="media"/> stands in <see cref="Kinds"/>.</summary>
    internal static int KindIndex(StorageMedia media) =>
        Array.IndexOf(KindOrder, media) is var index and >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(media), media, null);

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

    /// <summary>The routes in the order the route picker lists them, which is <see cref="ScanRoute"/>'s own.</summary>
    public static IReadOnlyList<string> RouteNames { get; } =
        [RouteName(ScanRoute.Auto), RouteName(ScanRoute.WalkOnly), RouteName(ScanRoute.Table)];

    /// <summary>A route, as the route picker names it.</summary>
    public static string RouteName(ScanRoute route) => route switch
    {
        ScanRoute.Auto => "Auto",
        ScanRoute.WalkOnly => "Walk folders only",
        ScanRoute.Table => "File table first",
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    /// <summary>
    /// The scan settings in one line, for the header of the section that folds them away: the route,
    /// and how many values were set rather than left on Auto, such as <c>Auto</c> or
    /// <c>Walk folders only, 2 values set</c>. A folded section that hid a changed value would leave
    /// the reader no way to tell a scan was tuned.
    /// </summary>
    public static string Summary(ScanPreferences preferences) =>
        preferences.ChosenValueCount is var chosen and > 0
            ? $"{RouteName(preferences.Route)}, {Counted.Of(chosen, "value", "values")} set"
            : RouteName(preferences.Route);

    /// <summary>
    /// What a scan of <paramref name="drive"/> runs with under <paramref name="preferences"/>, each
    /// value marked where Auto chose it, such as
    /// <c>C: (NVMe SSD): 16 threads (Auto), 16 KB listing buffer (Auto), 1,024 KB table reads (Auto),
    /// 8 in flight (Auto), 32 list reads in flight (Auto), lists read with the table (Auto), parsed on
    /// 4 threads (Auto)</c>. When the lists are read is always Auto's.
    /// </summary>
    public static string Describe(DriveKind drive, ScanPreferences preferences)
    {
        var tuning = VolumeTuning.Resolve(preferences, drive.Media);
        var chosen = tuning.Chosen;

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{drive.Drive} ({MediaName(tuning.Media)}): " +
            $"{tuning.Walk.Threads:N0} threads{AutoMark(chosen.WalkThreads)}, " +
            $"{tuning.Walk.ListingBufferBytes / 1024:N0} KB listing buffer{AutoMark(chosen.ListingBufferKiB)}, " +
            $"{tuning.Table.ReadBytes / 1024:N0} KB table reads{AutoMark(chosen.TableReadKiB)}, " +
            $"{tuning.Table.ReadsInFlight:N0} in flight{AutoMark(chosen.TableReadsInFlight)}, " +
            $"{tuning.Table.ListReadsInFlight:N0} list reads in flight{AutoMark(chosen.TableListReadsInFlight)}, " +
            $"{ListReads(tuning.Table)}{AutoMark(null)}, " +
            $"parsed on {tuning.Table.ParseThreads:N0} threads{AutoMark(preferences.TableParseThreads)}");
    }

    private static string ListReads(TableTuning table) => table.ListReads is AttributeListReads.BesideFirstPass
        ? "lists read with the table"
        : "lists read after the table";

    private static string AutoMark(int? chosen) => chosen is null ? " (Auto)" : string.Empty;
}
