using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// How long Claude Code keeps a conversation nobody uses before it deletes it itself: the
/// <c>cleanupPeriodDays</c> setting, 30 days unless the user changed it.
///
/// <para><b>Read so that every conversation offered can say when it would have gone anyway.</b> What a
/// user gains by removing one is the rest of that time, never the conversation's space for good, and a
/// row that let its bytes read as a lasting reclaim would misdescribe it.</para>
///
/// <para><b>Only the user's own settings are read.</b> A project's settings, a command line and an
/// organisation's managed settings can set the period as well, and each outranks the user's. The date
/// this gives is therefore the one the user's settings set, and the row says so rather than claim
/// more.</para>
/// </summary>
internal static class ClaudeCodeRetention
{
    public const long DefaultDays = 30;

    /// <summary>
    /// A hundred years. A longer period is a setting that means "keep everything", and no date past it is
    /// worth writing on a row.
    /// </summary>
    public const long LongestShownDays = 36_500;

    private const string SettingsFile = "settings.json";

    private const string Setting = "cleanupPeriodDays";

    /// <summary>Far past any settings file a person writes, and small enough to refuse one that is not.</summary>
    private const int MaximumSettingsBytes = 1024 * 1024;

    /// <summary>
    /// The period in days, or null where Deguffer cannot tell it: the settings file would not be read, is
    /// not JSON, or holds a value Claude Code itself would reject. Claude Code's minimum is one day, and it
    /// rejects zero. Any larger whole number is taken as it stands, however long.
    /// </summary>
    public static long? Of(string home)
    {
        var path = Path.Combine(home, SettingsFile);

        switch (LongPath.ProbeFile(path))
        {
            case PathPresence.Absent:
                return DefaultDays;

            case PathPresence.Refused:
                return null;
        }

        using var settings = BoundedJsonFile.Read(path, MaximumSettingsBytes);

        if (settings is null)
        {
            return null;
        }

        if (!settings.RootElement.TryGetProperty(Setting, out var value))
        {
            return DefaultDays;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var days) && days >= 1
            ? days
            : null;
    }
}
