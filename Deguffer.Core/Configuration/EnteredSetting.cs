using Deguffer.Core.Providers;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The whole number a setting stores, from the number a person typed into the box that sets it.
///
/// <para>Here rather than behind the settings page, because two of the three decide what gets
/// deleted, and one of those has a value that switches a safety rule off. A clamp that exists only
/// inside a view-model is one nothing can hold Deguffer to. The providers clamp again as they read,
/// because nothing validates <c>preferences.json</c> on the way in; this is the entry point, as
/// <see cref="Safety.MinimumAge.WithinHours"/> is for the hours.</para>
///
/// <para>The box reports a <see cref="double"/>, and an emptied box reports
/// <see cref="double.NaN"/>, which survives every comparison in
/// <see cref="Math.Clamp(double, double, double)"/>. So it is answered first, with the shipped
/// default: clearing a field is not a request for anything, and for two of these the floor is the
/// setting that deletes most.</para>
/// </summary>
public static class EnteredSetting
{
    /// <summary>
    /// A week, the most the guard on recently changed files may be set to. Past that the guard stops
    /// being "leave what is in use alone" and becomes a second, invisible answer to what Deguffer will
    /// ever delete, which is a decision the row it sits on does not make.
    /// </summary>
    public const int MaximumKeepHours = 168;

    /// <summary>The guard on recently changed files, in whole hours. Zero is off.</summary>
    public static int KeepHours(double entered) =>
        Whole(entered, AppPreferences.Default.KeepFilesChangedWithinHours, 0, MaximumKeepHours);

    /// <summary>
    /// How long something must sit untouched in a temporary folder before it is offered, in whole
    /// days. Zero is no age limit at all, which offers everything however recently it was written.
    /// </summary>
    public static int TemporaryFileAgeDays(double entered) =>
        Whole(
            entered,
            AppPreferences.Default.MinimumTemporaryFileAgeDays,
            TempDirectoryProvider.MinimumStaleDays,
            TempDirectoryProvider.MaximumStaleDays);

    /// <summary>
    /// How old a File History version must be before Windows may discard it, in whole days. The floor
    /// is <see cref="FileHistoryProvider.MinimumRetentionDays"/>, a safety rule: see there for what
    /// a retention of zero discards.
    /// </summary>
    public static int FileHistoryRetentionDays(double entered) =>
        Whole(
            entered,
            AppPreferences.Default.FileHistoryRetentionDays,
            FileHistoryProvider.MinimumRetentionDays,
            FileHistoryProvider.MaximumRetentionDays);

    /// <summary>
    /// <paramref name="entered"/> as a whole number between <paramref name="minimum"/> and
    /// <paramref name="maximum"/>.
    ///
    /// <para><b>Anything above zero and below one is one</b>, because in two of these settings zero is
    /// not the smallest window but the absence of one. Rounding alone does not keep a fraction off
    /// zero in either mode: <see cref="Math.Round(double)"/> sends a midpoint to even, so <c>0.5</c>
    /// becomes <b>0</b>, and <see cref="MidpointRounding.AwayFromZero"/> moves only the midpoint,
    /// leaving every value in <c>(0, 0.5)</c> on zero as well. Somebody typing <c>0.25</c> and meaning
    /// "a short window" would have stored the value that switches the guard off, without ever choosing
    /// it. Zero itself is a deliberate choice and passes through.</para>
    ///
    /// <para>Above that, a midpoint rounds away from zero, which in all three is the side that keeps
    /// more.</para>
    /// </summary>
    private static int Whole(double entered, int fallback, int minimum, int maximum)
    {
        if (double.IsNaN(entered))
        {
            return fallback;
        }

        var whole = entered is > 0 and < 1 ? 1 : Math.Round(entered, MidpointRounding.AwayFromZero);

        return (int)Math.Clamp(whole, minimum, maximum);
    }
}
