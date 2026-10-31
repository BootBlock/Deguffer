namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How the keyboard comes onto the map's shapes, by an arrow key, by the map taking the focus, or by
/// the menu key: which shape it starts on, and what it does once the drawing on screen has found that
/// shape (§7.1).
///
/// <para>Where the keyboard has not been moving between the shapes, what a click picked is what the
/// reader chose, so the keyboard starts on it. The menu key goes further: it acts on that pick and
/// nothing else, and not at all where a click picked nothing, because a menu opened on another shape
/// would act on something the reader never chose.</para>
/// </summary>
public readonly record struct MapFocusEntry
{
    private readonly bool _picks;

    private MapFocusEntry(int? startsOn, int? landsOnlyOn, bool moves, bool picks)
    {
        StartsOn = startsOn;
        LandsOnlyOn = landsOnlyOn;
        Moves = moves;
        _picks = picks;
    }

    /// <summary>The node a click picked that the keyboard starts on, or null where it starts on the shape it was on.</summary>
    public int? StartsOn { get; }

    /// <summary>The node the drawing has to find the keyboard on for the entry to act at all, or null for whichever it finds.</summary>
    public int? LandsOnlyOn { get; }

    /// <summary>Whether an arrow goes on to the next shape that way, rather than mark the shape the keyboard starts on.</summary>
    public bool Moves { get; }

    /// <summary>
    /// An arrow key on the map. Where the keyboard was moving, it goes on from where it is; where it was
    /// not, it starts on the one node a click picked, and goes on from there. The first arrow onto a map
    /// nothing is picked on marks the shape it starts on, rather than leave it.
    /// </summary>
    /// <param name="keyboard">Whether the keyboard was already moving between the shapes.</param>
    /// <param name="pickedAlone">The node picked where it is the only one, or null.</param>
    public static MapFocusEntry ByArrow(bool keyboard, int? pickedAlone) =>
        new(keyboard ? null : pickedAlone, landsOnlyOn: null, moves: keyboard || pickedAlone is not null, picks: false);

    /// <summary>
    /// The map takes the focus from the keyboard or a screen reader: the keyboard is on the shape it was
    /// on, or, where it was on none, the one node a click picked. Nothing is picked.
    /// </summary>
    /// <param name="focused">The node or block the keyboard was on, or null where it was on none.</param>
    /// <param name="pickedAlone">The node picked where it is the only one, or null.</param>
    public static MapFocusEntry OnFocus(int? focused, int? pickedAlone) =>
        new(focused is null ? pickedAlone : null, landsOnlyOn: null, moves: false, picks: false);

    /// <summary>
    /// The menu key or Shift+F10: on the shape the keyboard is on where it was moving, and picked first
    /// as a right-click picks. Where it was not, on the one node a click picked and nothing else; and
    /// null, for no menu, where a click picked nothing.
    /// </summary>
    /// <param name="keyboard">Whether the keyboard was already moving between the shapes.</param>
    /// <param name="pickedAlone">The node picked where it is the only one, or null.</param>
    public static MapFocusEntry? ByMenu(bool keyboard, int? pickedAlone) =>
        keyboard ? new(startsOn: null, landsOnlyOn: null, moves: false, picks: true)
        : pickedAlone is { } clicked ? new(clicked, clicked, moves: false, picks: true)
        : null;

    /// <summary>Whether the entry acts on <paramref name="found"/>, the shape the drawing found the keyboard on.</summary>
    public bool Accepts(MapShape found) => LandsOnlyOn is not { } node || found.Node == node;

    /// <summary>
    /// Whether the shape the keyboard lands on is picked: by the menu always, and by an arrow where it
    /// <paramref name="moved"/>, as a list's arrow keys pick the row they reach.
    /// </summary>
    public bool Picks(bool moved) => _picks || moved;
}
