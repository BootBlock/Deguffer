namespace Deguffer.Core.Configuration;

/// <summary>
/// Which theme the user asked for. <see cref="System"/> is the §6.5 default — follow the system
/// setting unless told otherwise.
/// </summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// The user's settings, as a value. Most of it is presentation-only — §6.5 makes the backdrop
/// decoration, so switching it off changes nothing about what Deguffer will delete — but the two
/// confirmation settings govern what is asked before a deletion, so they are read by Core rather
/// than only by the shell.
/// </summary>
/// <param name="Theme">Light, dark, or follow the system.</param>
/// <param name="BackdropEnabled">
/// Whether to ask for the Acrylic backdrop. High contrast overrides this to off regardless — the
/// backdrop fights the user's stated accessibility requirement, and that is not negotiable by a
/// preference.
/// </param>
/// <param name="ConfirmBeforeCleaning">
/// Whether Clean raises a confirmation naming what is about to go — covering exactly the rows §7
/// does not ask about itself, so nothing is confirmed twice and nothing goes unconfirmed. §7
/// already makes preview the primary action; this is the second belt for a step that has no undo,
/// and the only prompt a Tier 1 selection gets.
/// </param>
/// <param name="RequireTypedConfirmation">
/// Whether §7's typed phrase is demanded of Tier 3. Off by default: typing a provider's name for
/// every emptied Recycle Bin becomes a transcription chore rather than a decision, and a gate the
/// user resents is a gate they learn to get past without reading.
///
/// Switching it off retires Tier 3's <em>own</em> question, not the question. The row then falls to
/// <paramref name="ConfirmBeforeCleaning"/>, which names it and says the deletion cannot be undone.
/// Turning both off is a deliberate pair of choices, and leaves the preview and Tier 3 never being
/// pre-selected as what stands between the user and the deletion.
/// </param>
public sealed record AppPreferences(
    AppTheme Theme = AppTheme.System,
    bool BackdropEnabled = true,
    bool ConfirmBeforeCleaning = true,
    bool RequireTypedConfirmation = false)
{
    public static readonly AppPreferences Default = new();
}
