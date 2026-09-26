namespace Deguffer.Core.Memory.Acting;

/// <summary>
/// What the user picked out by hand on a memory view: one node of one reading, the program it is
/// where it is one, and the reading it was picked from.
///
/// <para>In Core rather than in the page for the reason <see cref="MemoryTarget"/> is: what a pick
/// is about, and when it stops being about anything, decides which program §7.2.1's one action is
/// aimed at. A rule that exists only inside a view-model is a rule nothing can hold Deguffer to.</para>
///
/// <para><b>Only a hand picks.</b> Nothing here chooses a node, and nothing carries a pick onto
/// something the user did not pick: a pick that cannot be found again is dropped, never replaced
/// (§7.2).</para>
/// </summary>
public sealed record MemoryPick
{
    private MemoryPick(MemoryTree tree, int node, ProcessMemory? target, MemorySnapshot pickedFrom)
    {
        Tree = tree;
        Node = node;
        Target = target;
        PickedFrom = pickedFrom;
    }

    /// <summary>The tree <see cref="Node"/> is a number of, which is the one on screen.</summary>
    public MemoryTree Tree { get; }

    public int Node { get; }

    /// <summary>
    /// The program the pick is, or null where the node is a part of the picture rather than a
    /// program (<see cref="MemoryTarget.Of"/>).
    /// </summary>
    public ProcessMemory? Target { get; }

    /// <summary>
    /// The reading the user picked from, kept across every reading that carries the pick. §7.2.1
    /// identifies a process by its identifier and creation time in the snapshot the user picked from,
    /// and asks about it against that snapshot rather than against whichever reading is newest.
    /// </summary>
    public MemorySnapshot PickedFrom { get; }

    /// <summary>
    /// The answer that needs no question of Windows: a node that is not a program is refused as
    /// such, and nothing is opened to learn it. Null where the policy has to be asked.
    /// </summary>
    public MemoryVerdict? Settled => Target is null ? MemoryTarget.NotAProgram : null;

    /// <summary>
    /// What <paramref name="node"/> of <paramref name="tree"/> is as a pick, or null where it is not
    /// a node of that tree at all. A number outside the tree is one held over from a reading that has
    /// been replaced, and picks nothing.
    /// </summary>
    public static MemoryPick? Of(MemoryTree? tree, int? node) =>
        tree is not null && node is { } picked && tree.Holds(picked)
            ? new MemoryPick(tree, picked, MemoryTarget.Of(tree, picked), tree.Snapshot)
            : null;

    /// <summary>
    /// This pick once the view is shown <paramref name="arriving"/>, or null where it is dropped.
    ///
    /// <para><b>A reading carries it and a navigation drops it.</b> A program picked in one part of the
    /// tree is not picked in the next, and a reading is the same subject measured again: dropping the
    /// pick on every reading would make a program impossible to pick at all on a page that reads
    /// twice a second.</para>
    ///
    /// <para>Carried by <see cref="MemoryPlace"/>'s rule, identifier and creation time, because every
    /// reading renumbers the nodes and a number carried across would point at whatever sits there
    /// now. The reading it was picked from goes with it unchanged.</para>
    /// </summary>
    public MemoryPick? After(MemoryViewChange change, MemoryTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        return change == MemoryViewChange.Reading && MemoryPlace.TryCarry(Tree, Node, arriving) is { } carried
            ? new MemoryPick(arriving, carried, Target, PickedFrom)
            : null;
    }

    /// <summary>
    /// The program a press may go on to ask, or null where <paramref name="verdict"/> does not allow
    /// it. Only an allowed verdict opens the confirmation; a refusal, and a verdict still on its way,
    /// are answered with a sentence instead (§7.2.1).
    /// </summary>
    public ProcessMemory? ToClose(MemoryVerdict? verdict) => verdict is { IsAllowed: true } ? Target : null;
}
