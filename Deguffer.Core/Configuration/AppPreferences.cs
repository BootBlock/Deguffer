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
/// How much of each finding row the Storage page draws. <see cref="Compact"/> is the shipped view:
/// the list is a set of rows to choose between, and a screen that shows all of them beats one that
/// shows six and explains each.
///
/// Compact hides §7's "what happens on next use" sentence rather than retiring it. The sentence is
/// on the row's tooltip and inside its disclosure, alongside the plan, the per-step figures and
/// §5.2's "what was left alone" notes. <see cref="Standard"/> is the same list with that sentence
/// written out under every name.
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
/// default, so it is the one a user arrives already able to read. The other three are not
/// decoration beside it. <see cref="List"/> is the densest answer to "what is biggest", which is
/// the question most people actually open the page with. <see cref="Icicle"/> keeps area exactly
/// proportional at every level, has room to label several levels at once, and is the one picture
/// that stays still while a scan is filling it in — which is why the map draws it, whichever of the
/// four is chosen, until the scan finishes. <see cref="Sunburst"/> is what the rest of this category
/// ships and the easiest of the four to point at, at the cost of exaggerating whatever is deepest —
/// an outer ring gives more area for the same proportion, because area grows with the square of the
/// radius.</para>
///
/// <para>The values are ordinal, and the view picker lists them in this order. They are stored by
/// name rather than by number, so the order is a presentation decision and not a compatibility
/// one.</para>
/// </summary>
public enum ExploreView
{
    Treemap = 0,
    Icicle = 1,
    Sunburst = 2,
    List = 3,
}

/// <summary>
/// What the colours on the Explore map say.
///
/// <para>Orthogonal to <see cref="ExploreView"/> rather than a fifth entry in it, because it is a
/// different question: the view decides where the shapes go, and this decides what painting one of
/// them a colour tells the reader. Every combination of the two is meaningful, and folding them
/// into one list would offer six choices where there are two.</para>
///
/// <para><see cref="Branch"/> is the shipped one and answers "what is this part of", which is the
/// question a map of a drive is opened with. <see cref="Age"/> answers §8's first open question
/// instead — whether a toolchain is idle — which a size picture cannot show at all: an Android SDK
/// and a working project look identical by size, and the whole difference is that nothing has
/// written to one of them in two years.</para>
/// </summary>
public enum ExploreColouring
{
    /// <summary>A hue per top-level branch, shaded by depth. See <c>TilePalette</c>.</summary>
    Branch = 0,

    /// <summary>
    /// A band per age, by the newest write anywhere at or below the shape. See <c>AgePalette</c>,
    /// and <c>ExploreTree.ModifiedOf</c> for why it is the newest write below rather than the
    /// shape's own date.
    /// </summary>
    Age = 1,
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
/// <param name="ExploreColours">
/// What the colours in that picture say. Presentation only on the same terms — it changes what the
/// reader can see at a glance, never what was measured.
/// </param>
/// <param name="ShowNotInstalled">
/// Whether to list a provider whose toolchain is not on this machine. Off by default: such a row
/// has nothing to reclaim and nothing to tick, so it lengthens the list without adding a decision
/// to it. Presentation only, and it hides rather than skips — every provider is still scanned, so
/// switching it on lists them with no rescan, and one that turns out to be installed after all is
/// never hidden by it.
/// </param>
/// <param name="ShowAlreadyClear">
/// Whether to list a location that is installed, readable and has nothing to reclaim. Off by
/// default: the row states a fact about the machine and offers no decision, so a set of them
/// pushes the rows that do carry one off the screen.
///
/// Only the rows that actually say "Already clear". A location Windows would not let Deguffer
/// list, and one whose every file is inside the guard on recently changed files, both measure
/// zero and are not clear at all — each says so in its own words, and neither is hidden by this.
///
/// Presentation only, and it hides rather than skips, on the same terms as
/// <paramref name="ShowNotInstalled"/>: every provider is still scanned, so switching it on lists
/// them with no rescan.
/// </param>
/// <param name="ExploreNotesDismissed">
/// Whether Explore's notes are collapsed to the button that brings them back. Off by default, and
/// only the reader ever turns it on.
///
/// <para>Stored rather than held for the session, because it answers "I have read these" rather
/// than "not just now". Somebody who has decided the walked-scan sentence is not for them should
/// not have to decide it again at every launch.</para>
///
/// <para>Presentation only, and it collapses rather than silences: the button stands in the notes'
/// own corner whenever any of them has something to say, so §7.1's refusal and §5.5's route
/// sentence stay one activation away rather than going. What it cannot do is arrive over the
/// picture uninvited, which is the whole of what it was asked for.</para>
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
/// <param name="KeepFilesChangedWithinHours">
/// Leave any file touched inside this many hours where it is, however the row it sits in is
/// classified. Zero is off, and off is the default.
///
/// <para>One of the two settings that change what gets deleted rather than how the window looks,
/// the approved source folders being the other, and the only one that can make a plan smaller than
/// what is actually on the disk. §5.3 already refuses anything Windows
/// is holding open, which covers a process with a file still on it — this covers the one that
/// wrote a file, closed it, and will want it again in an hour. Nothing distinguishes such a file
/// from a stale one by name, place or size, so an age is the only signal there is.</para>
///
/// <para>Stored as whole hours because that is what the user chose, not as the instant it becomes:
/// the instant is fixed once per preview, so that the clean deletes exactly the files the preview
/// said it would. See <see cref="Safety.MinimumAge"/>.</para>
/// </param>
public sealed record AppPreferences(
    AppTheme Theme = AppTheme.System,
    ViewDensity View = ViewDensity.Compact,
    ExploreView Explore = ExploreView.Treemap,
    ExploreColouring ExploreColours = ExploreColouring.Branch,
    bool ExploreNotesDismissed = false,
    bool ShowNotInstalled = false,
    bool ShowAlreadyClear = false,
    bool BackdropEnabled = true,
    bool ConfirmBeforeCleaning = true,
    bool RequireTypedConfirmation = false,
    int KeepFilesChangedWithinHours = 0)
{
    public static readonly AppPreferences Default = new();
}
