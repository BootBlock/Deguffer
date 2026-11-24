using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The five scan values the Settings page sets for one kind of drive, each against its shipped value,
/// which is Auto. A kind's reset leaves every other kind's values as they were, because a value suited
/// to one kind can slow another.
/// </summary>
public sealed class KindSettingDefaults
{
    private KindSettingDefaults(
        SettingDefault walkThreads,
        SettingDefault listingBufferKiB,
        SettingDefault tableReadKiB,
        SettingDefault tableReadsInFlight,
        SettingDefault tableListReadsInFlight)
    {
        WalkThreads = walkThreads;
        ListingBufferKiB = listingBufferKiB;
        TableReadKiB = tableReadKiB;
        TableReadsInFlight = tableReadsInFlight;
        TableListReadsInFlight = tableListReadsInFlight;
        All = [walkThreads, listingBufferKiB, tableReadKiB, tableReadsInFlight, tableListReadsInFlight];
    }

    public SettingDefault WalkThreads { get; }

    public SettingDefault ListingBufferKiB { get; }

    public SettingDefault TableReadKiB { get; }

    public SettingDefault TableReadsInFlight { get; }

    public SettingDefault TableListReadsInFlight { get; }

    /// <summary>The five in the order the page lists them.</summary>
    public IReadOnlyList<SettingDefault> All { get; }

    internal static KindSettingDefaults For(StorageMedia media)
    {
        var kind = ScanTuningText.MediaName(media);

        return new KindSettingDefaults(
            Value(media, kind, "Folders read at once", chosen => chosen.WalkThreads, (chosen, value) => chosen with { WalkThreads = value }, SettingValueText.Threads),
            Value(media, kind, "Listing buffer", chosen => chosen.ListingBufferKiB, (chosen, value) => chosen with { ListingBufferKiB = value }, SettingValueText.Kilobytes),
            Value(media, kind, "File table read size", chosen => chosen.TableReadKiB, (chosen, value) => chosen with { TableReadKiB = value }, SettingValueText.Kilobytes),
            Value(media, kind, "File table reads at once", chosen => chosen.TableReadsInFlight, (chosen, value) => chosen with { TableReadsInFlight = value }, SettingValueText.Reads),
            Value(media, kind, "Attribute list reads at once", chosen => chosen.TableListReadsInFlight, (chosen, value) => chosen with { TableListReadsInFlight = value }, SettingValueText.Reads));
    }

    private static SettingDefault Value(
        StorageMedia media,
        string kind,
        string title,
        Func<MediaScanPreferences, int?> read,
        Func<MediaScanPreferences, int?, MediaScanPreferences> write,
        Func<int?, string> say) =>
        SettingDefault.Of(
            title,
            kind,
            preferences => read(preferences.Scanning.For(media)),
            (preferences, value) => preferences with
            {
                Scanning = preferences.Scanning.With(media, write(preferences.Scanning.For(media), value)),
            },
            say);
}
