namespace Deguffer.Core.Exploring;

/// <summary>
/// The four notes the Explore page can put on its card, and whether the card shows them or the
/// button that brings them back.
/// </summary>
/// <param name="Selection">Whether the selection has something to say about what is picked.</param>
/// <param name="Stale">
/// Whether a removal has made the sizes on screen leave out what went. §7.1 lets Explore's totals be
/// lower bounds "provided the picture states which it is", and after a removal this note is the whole
/// of that statement.
/// </param>
/// <param name="View">Whether the picture substitutes for the view asked for (<see cref="Rendering.ExploreDisplay.Note"/>).</param>
/// <param name="Route">Whether §5.5 asks for a sentence beside a walked scan (<see cref="ExploreRouteText.Describe"/>).</param>
/// <param name="Dismissed">
/// Whether the reader has collapsed the notes into their button. Theirs to set and nobody else's, so
/// a note arriving afterwards does not put the panel back over the picture: it brings the button
/// back, which is where the sentence then is.
/// </param>
public readonly record struct ExploreNotes(bool Selection, bool Stale, bool View, bool Route, bool Dismissed)
{
    /// <summary>Whether any of the four has something to say.</summary>
    public bool HasAny => Selection || Stale || View || Route;

    /// <summary>Whether the notes are on the card. See <see cref="ShowsButton"/> for the other half.</summary>
    public bool ShowsPanel => HasAny && !Dismissed;

    /// <summary>
    /// Whether the button that brings the notes back is on the card.
    ///
    /// <para>The two are exclusive, and both are false while there is nothing to read. That is what
    /// makes the button itself the signal: it is in the notes' corner exactly when a sentence is
    /// waiting behind it, so a collapsed panel with something to say never looks like a collapsed
    /// panel with nothing.</para>
    /// </summary>
    public bool ShowsButton => HasAny && Dismissed;

    /// <summary>
    /// Whether one of the notes is a warning, which the collapsed button has to say in its glyph and
    /// its name.
    ///
    /// <para>Only the stale note is. Collapsed behind the same button as the other three it would
    /// arrive with nothing on screen changing, because the route note usually has the button on
    /// screen already.</para>
    /// </summary>
    public bool Warns => Stale;
}
