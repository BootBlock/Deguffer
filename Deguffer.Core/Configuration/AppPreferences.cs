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
/// How much of each finding row the Storage page draws. <see cref="Standard"/> is the shipped
/// view: §7 wants every row to state what happens on next use, and only the full row has room
/// for that sentence.
///
/// <see cref="Compact"/> is the user asking for the list to be scannable instead — name, tier and
/// size on one line. It hides the sentence rather than retiring it: the row keeps its disclosure,
/// so the plan, the per-step figures and §5.2's "what was left alone" notes are one click away in
/// either view.
/// </summary>
public enum ViewDensity
{
    Standard = 0,
    Compact = 1,
}

/// <summary>
/// How the Explore page draws what it found.
///
/// <para><see cref="Treemap"/> is the shipped view: it is what every tool in this category shows by
/// default, so it is the one a user arrives already able to read. The other two are not decoration
/// beside it. <see cref="List"/> is the densest answer to "what is biggest", which is the question
/// most people actually open the page with, and it is the only view that stays readable while a
/// scan is still filling it in. <see cref="Icicle"/> keeps area exactly proportional at every level
/// and has room to label several levels at once, which a treemap does not.</para>
/// </summary>
public enum ExploreView
{
    Treemap = 0,
    Icicle = 1,
    List = 2,
}

/// <summary>
/// The user's settings, as a value. Most of it is presentation-only — §6.5 makes the backdrop
/// decoration, so switching it off changes nothing about what Deguffer will delete — but the two
/// confirmation settings govern what is asked before a deletion, so they are read by Core rather
/// than only by the shell.
/// </summary>
/// <param name="Theme">Light, dark, or follow the system.</param>
/// <param name="View">
/// How much of each finding row the Storage page draws. Presentation only — it changes what is on
/// screen, never what is scanned, offered or deleted.
/// </param>
/// <param name="Explore">
/// How the Explore page draws a scanned volume. Presentation only, in the same way
/// <paramref name="View"/> is: it changes which picture is on screen, never what was scanned and
/// never what may be removed.
/// </param>
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
/// <paramref name="ConfirmBeforeCleaning"/>, which names it and quotes the same
/// <see cref="Execution.ConfirmationRequirement.ConsequenceOf"/> sentence the typed dialog would
/// have carried — §7 leaves how hard the confirmation is to give to the user, and never left saying
/// what is unrecoverable to anybody. Turning both off is a deliberate pair of choices, and leaves
/// the preview and Tier 3 never being pre-selected as what stands between the user and the
/// deletion.
/// </param>
public sealed record AppPreferences(
    AppTheme Theme = AppTheme.System,
    ViewDensity View = ViewDensity.Standard,
    ExploreView Explore = ExploreView.Treemap,
    bool BackdropEnabled = true,
    bool ConfirmBeforeCleaning = true,
    bool RequireTypedConfirmation = false)
{
    public static readonly AppPreferences Default = new();
}
