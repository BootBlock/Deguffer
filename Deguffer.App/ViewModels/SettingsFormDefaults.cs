using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The default of each value the Settings page's form sets, one row each, so a row can state its
/// shipped value and offer the way back to it. What each default is, is Core's
/// <see cref="ShippedSettings"/>. Each row is named for its setting there.
/// </summary>
public sealed class SettingsFormDefaults
{
    private readonly SettingReset[] _rows;

    /// <param name="form">The form whose rows these are, which says on each row when its write failed.</param>
    /// <param name="reset">The form's write of one setting's default, reported on the row that asked.</param>
    internal SettingsFormDefaults(PreferenceService preferences, SettingsViewModel form, Action<SettingSave, SettingDefault> reset)
    {
        SettingReset Row(SettingDefault setting, SettingSave save) =>
            new(preferences, () => setting, shipped => reset(save, shipped));

        _rows =
        [
            Theme = Row(ShippedSettings.Theme, form.ThemeSave),
            BackdropEnabled = Row(ShippedSettings.BackdropEnabled, form.BackdropEnabledSave),
            TreemapSpacing = Row(ShippedSettings.TreemapSpacing, form.TreemapSpacingSave),
            ShowAlreadyClear = Row(ShippedSettings.ShowAlreadyClear, form.ShowAlreadyClearSave),
            SpotlightCleanedRow = Row(ShippedSettings.SpotlightCleanedRow, form.SpotlightCleanedRowSave),
            ConfirmBeforeCleaning = Row(ShippedSettings.ConfirmBeforeCleaning, form.ConfirmBeforeCleaningSave),
            RequireTypedConfirmation = Row(ShippedSettings.RequireTypedConfirmation, form.RequireTypedConfirmationSave),
            KeepFilesChangedWithinHours = Row(ShippedSettings.KeepFilesChangedWithinHours, form.KeepFilesChangedWithinHoursSave),
            MinimumTemporaryFileAge = Row(ShippedSettings.MinimumTemporaryFileAge, form.MinimumTemporaryFileAgeSave),
            FileHistoryRetentionDays = Row(ShippedSettings.FileHistoryRetentionDays, form.FileHistoryRetentionDaysSave),
            EmptyRecycleBinsDirectly = Row(ShippedSettings.EmptyRecycleBinsDirectly, form.EmptyRecycleBinsDirectlySave),
        ];
    }

    public SettingReset Theme { get; }

    public SettingReset BackdropEnabled { get; }

    public SettingReset TreemapSpacing { get; }

    public SettingReset ShowAlreadyClear { get; }

    public SettingReset SpotlightCleanedRow { get; }

    public SettingReset ConfirmBeforeCleaning { get; }

    public SettingReset RequireTypedConfirmation { get; }

    public SettingReset KeepFilesChangedWithinHours { get; }

    public SettingReset MinimumTemporaryFileAge { get; }

    public SettingReset FileHistoryRetentionDays { get; }

    public SettingReset EmptyRecycleBinsDirectly { get; }

    /// <summary>
    /// Read every row's default again. The form does so after each of its own writes, and the page after
    /// every write, the scanning section's included. A row is told only where its answer changed, so
    /// the second reading of one write tells it nothing.
    /// </summary>
    public void Follow()
    {
        foreach (var row in _rows)
        {
            row.Refresh();
        }
    }
}
