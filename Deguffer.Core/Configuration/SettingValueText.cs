using Deguffer.Core.Text;

namespace Deguffer.Core.Configuration;

/// <summary>
/// A setting's value in the words its control shows, so the default a row states and the list of what
/// a restore changes read as the control does: <c>7 days</c> beside a box showing 7 and the unit
/// "days", <c>On</c> beside a switch, and the item a combo box lists.
/// </summary>
public static class SettingValueText
{
    /// <summary>The theme picker's items, in <see cref="AppTheme"/>'s order, which is the order the page indexes them by.</summary>
    public static IReadOnlyList<string> ThemeNames { get; } =
        [Theme(AppTheme.System), Theme(AppTheme.Light), Theme(AppTheme.Dark)];

    /// <summary>The treemap spacing picker's items, in <see cref="ExploreSpacing"/>'s order.</summary>
    public static IReadOnlyList<string> SpacingNames { get; } =
        [Spacing(ExploreSpacing.Dense), Spacing(ExploreSpacing.Comfortable), Spacing(ExploreSpacing.Spacious)];

    public static string Theme(AppTheme theme) => theme switch
    {
        AppTheme.System => "Use system setting",
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
    };

    public static string Spacing(ExploreSpacing spacing) => spacing switch
    {
        ExploreSpacing.Dense => "Dense",
        ExploreSpacing.Comfortable => "Comfortable",
        ExploreSpacing.Spacious => "Spacious",
        _ => throw new ArgumentOutOfRangeException(nameof(spacing), spacing, null),
    };

    /// <summary>A switch's state, in the words the switch shows beside itself.</summary>
    public static string Switch(bool on) => on ? "On" : "Off";

    public static string Hours(int hours) => Counted.Of(hours, "hour", "hours");

    public static string Days(int days) => Counted.Of(days, "day", "days");

    public static string Threads(int? chosen) => AutoOr(chosen, threads => Counted.Of(threads, "thread", "threads"));

    public static string Reads(int? chosen) => AutoOr(chosen, reads => Counted.Of(reads, "read", "reads"));

    public static string KiB(int? chosen) => AutoOr(chosen, kib => $"{kib:N0} KB");

    /// <summary>A scan value, which is Auto where nobody chose one.</summary>
    private static string AutoOr(int? chosen, Func<int, string> say) => chosen is { } value ? say(value) : "Auto";
}
