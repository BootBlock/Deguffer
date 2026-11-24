using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The default of each value the Settings page's Scanning section sets, one row each. The five under
/// the kind picker are the defaults of the kind it names, so a reset puts back that kind's value and no
/// other. What each default is, is Core's <see cref="ShippedSettings"/> and
/// <see cref="KindSettingDefaults"/>, and each row is named for its setting there.
/// </summary>
public sealed class ScanSettingsDefaults
{
    private readonly SettingReset[] _rows;

    /// <param name="section">The section whose rows these are, which says on each row when its write failed.</param>
    /// <param name="kind">The kind of drive the picker names now.</param>
    /// <param name="reset">The section's write of one value's default, reported on the row that asked.</param>
    internal ScanSettingsDefaults(
        PreferenceService preferences,
        ScanSettingsViewModel section,
        Func<StorageMedia> kind,
        Action<SettingSave, SettingDefault> reset)
    {
        SettingReset Row(Func<SettingDefault> setting, SettingSave save) =>
            new(preferences, setting, shipped => reset(save, shipped));

        _rows =
        [
            Route = Row(() => ShippedSettings.Route, section.RouteSave),
            TableParseThreads = Row(() => ShippedSettings.TableParseThreads, section.TableParseThreadsSave),
            WalkThreads = Row(() => ShippedSettings.ForKind(kind()).WalkThreads, section.WalkThreadsSave),
            ListingBufferKiB = Row(() => ShippedSettings.ForKind(kind()).ListingBufferKiB, section.ListingBufferSave),
            TableReadKiB = Row(() => ShippedSettings.ForKind(kind()).TableReadKiB, section.TableReadSave),
            TableReadsInFlight = Row(() => ShippedSettings.ForKind(kind()).TableReadsInFlight, section.TableReadsInFlightSave),
            TableListReadsInFlight = Row(() => ShippedSettings.ForKind(kind()).TableListReadsInFlight, section.TableListReadsInFlightSave),
        ];
    }

    public SettingReset Route { get; }

    public SettingReset TableParseThreads { get; }

    public SettingReset WalkThreads { get; }

    public SettingReset ListingBufferKiB { get; }

    public SettingReset TableReadKiB { get; }

    public SettingReset TableReadsInFlight { get; }

    public SettingReset TableListReadsInFlight { get; }

    /// <summary>
    /// Read every row's default again, after a write or a move of the kind picker. A row is told only
    /// where its answer changed.
    /// </summary>
    public void Follow()
    {
        foreach (var row in _rows)
        {
            row.Refresh();
        }
    }
}
