using Deguffer.Core.Configuration;

namespace Deguffer.Core.Providers;

/// <summary>
/// §5.3's age filter for anything offered from a temporary folder, read from the one setting that
/// states it.
///
/// <para><b>Shared because it is one rule, not two that happen to agree.</b> The setting says how
/// long something must sit untouched in a temporary folder before Deguffer offers it, and the
/// temporary files row and the test browser profiles row both offer from one. A second copy of the
/// clamp or the wording is how one row would come to quote a cut-off the other does not apply.</para>
/// </summary>
internal static class TemporaryAgeLimit
{
    /// <summary>
    /// The age limit in force, in whole days, clamped because nothing validates
    /// <c>preferences.json</c> on the way in. Read at the moment it is needed, so a change in
    /// Settings takes effect from the next preview.
    /// </summary>
    public static int Days(ICurrentPreferences preferences) => Math.Clamp(
        preferences.Current.MinimumTemporaryFileAgeDays,
        TempDirectoryProvider.MinimumStaleDays,
        TempDirectoryProvider.MaximumStaleDays);

    /// <summary>
    /// A whole number of days as the phrase a row prints. <see cref="Safety.MinimumAge.Describe"/>
    /// answers for a window that is on; this one is asked before there is a window, and about a
    /// value that may be zero.
    /// </summary>
    public static string Describe(int days) => days == 1 ? "a day" : $"{days} days";
}
