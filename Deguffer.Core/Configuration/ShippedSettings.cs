using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Every setting the Settings page shows, each against the value Deguffer ships with, so a row can
/// state its default and put itself back, and the page can put all of them back at once.
///
/// <para>Only the settings on that page. The keep list, the source folders and the emulator folders
/// are lists the reader built one entry at a time, kept in files of their own, and nothing here
/// reaches them. Preferences another page sets where they are seen, such as the Storage page's order,
/// are left as they are, because a reader restoring the Settings page has not looked at them. So is how
/// a map is drawn, which the map appearance window sets and puts back with a restore of its own. Only the
/// treemap spacing is in both places, and it is on this page.</para>
///
/// <para><c>ShippedSettingsMarkupTests</c> holds this list and the page's rows to each other, by the
/// member name each row's default and control are bound to.</para>
/// </summary>
public static class ShippedSettings
{
    public static readonly SettingDefault Theme = SettingDefault.Of(
        "Theme", null, preferences => preferences.Theme, (preferences, value) => preferences with { Theme = value }, SettingValueText.Theme);

    public static readonly SettingDefault BackdropEnabled = Switch(
        "Translucent background", preferences => preferences.BackdropEnabled, (preferences, value) => preferences with { BackdropEnabled = value });

    public static readonly SettingDefault TreemapSpacing = SettingDefault.Of(
        "Treemap spacing", null, preferences => preferences.TreemapSpacing, (preferences, value) => preferences with { TreemapSpacing = value }, SettingValueText.Spacing);

    public static readonly SettingDefault ShowAlreadyClear = Switch(
        "Show items that are already clear", preferences => preferences.ShowAlreadyClear, (preferences, value) => preferences with { ShowAlreadyClear = value });

    public static readonly SettingDefault SpotlightCleanedRow = Switch(
        "Follow the clean down the list", preferences => preferences.SpotlightCleanedRow, (preferences, value) => preferences with { SpotlightCleanedRow = value });

    public static readonly SettingDefault ConfirmBeforeCleaning = Switch(
        "Confirm before cleaning", preferences => preferences.ConfirmBeforeCleaning, (preferences, value) => preferences with { ConfirmBeforeCleaning = value });

    public static readonly SettingDefault RequireTypedConfirmation = Switch(
        "Type a name to delete user data", preferences => preferences.RequireTypedConfirmation, (preferences, value) => preferences with { RequireTypedConfirmation = value });

    public static readonly SettingDefault KeepFilesChangedWithinHours = SettingDefault.Of(
        "Leave recently changed files alone",
        null,
        preferences => preferences.KeepFilesChangedWithinHours,
        (preferences, value) => preferences with { KeepFilesChangedWithinHours = value },
        SettingValueText.Hours);

    public static readonly SettingDefault MinimumTemporaryFileAge = SettingDefault.Of(
        "Only offer temporary files older than",
        null,
        preferences => preferences.MinimumTemporaryFileAgeDays,
        (preferences, value) => preferences with { MinimumTemporaryFileAgeDays = value },
        SettingValueText.Days);

    public static readonly SettingDefault FileHistoryRetentionDays = SettingDefault.Of(
        "Keep File History versions for",
        null,
        preferences => preferences.FileHistoryRetentionDays,
        (preferences, value) => preferences with { FileHistoryRetentionDays = value },
        SettingValueText.Days);

    public static readonly SettingDefault EmptyRecycleBinsDirectly = Switch(
        "Empty Recycle Bins without Windows", preferences => preferences.EmptyRecycleBinsDirectly, (preferences, value) => preferences with { EmptyRecycleBinsDirectly = value });

    public static readonly SettingDefault Route = SettingDefault.Of(
        "How to scan",
        null,
        preferences => preferences.Scanning.Route,
        (preferences, value) => preferences with { Scanning = preferences.Scanning with { Route = value } },
        ScanTuningText.RouteName);

    public static readonly SettingDefault TableParseThreads = SettingDefault.Of(
        "File table parse threads",
        null,
        preferences => preferences.Scanning.TableParseThreads,
        (preferences, value) => preferences with { Scanning = preferences.Scanning with { TableParseThreads = value } },
        SettingValueText.Threads);

    /// <summary>Each kind's five values, built once, in the order the kind picker lists the kinds.</summary>
    private static readonly KindSettingDefaults[] Kinds = [.. ScanTuningText.Kinds.Select(KindSettingDefaults.For)];

    /// <summary>Every setting the page shows, in the order it shows them, each kind of drive's values in the picker's order.</summary>
    public static IReadOnlyList<SettingDefault> All { get; } =
    [
        Theme,
        BackdropEnabled,
        TreemapSpacing,
        ShowAlreadyClear,
        SpotlightCleanedRow,
        ConfirmBeforeCleaning,
        RequireTypedConfirmation,
        KeepFilesChangedWithinHours,
        MinimumTemporaryFileAge,
        FileHistoryRetentionDays,
        EmptyRecycleBinsDirectly,
        Route,
        TableParseThreads,
        .. Kinds.SelectMany(kind => kind.All),
    ];

    /// <summary>The five values the page sets for <paramref name="media"/>.</summary>
    public static KindSettingDefaults ForKind(StorageMedia media) => Kinds[ScanTuningText.KindIndex(media)];

    /// <summary>Whether <paramref name="preferences"/> hold any setting on the page off its shipped value.</summary>
    public static bool AnyChanged(AppPreferences preferences)
    {
        foreach (var setting in All)
        {
            if (!setting.IsDefault(preferences))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Each setting <paramref name="preferences"/> hold off its shipped value, in the page's order.</summary>
    public static IReadOnlyList<SettingDefault> Changed(AppPreferences preferences) =>
        [.. All.Where(setting => !setting.IsDefault(preferences))];

    /// <summary>
    /// <paramref name="preferences"/> with every setting on the page at its shipped value, and every
    /// preference the page does not show as it was.
    /// </summary>
    public static AppPreferences RestoreAll(AppPreferences preferences) =>
        All.Aggregate(preferences, (restored, setting) => setting.Reset(restored));

    private static SettingDefault Switch(string title, Func<AppPreferences, bool> read, Func<AppPreferences, bool, AppPreferences> write) =>
        SettingDefault.Of(title, null, read, write, SettingValueText.Switch);
}
