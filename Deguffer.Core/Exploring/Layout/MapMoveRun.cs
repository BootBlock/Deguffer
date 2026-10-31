namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which of the map's moves is under way, one at a time: a move starts only once the one before it
/// has finished, and the end of a move that was finished early, or replaced, finishes nothing.
///
/// <para>The compositor reports a move's animations over after the fact, and a move can be finished
/// early in the meantime (a press, a new folder, the next move). That late report must not end the
/// move started since, so each start is numbered and only the current one's report ends it.</para>
/// </summary>
public sealed class MapMoveRun
{
    /// <summary>The number of the last move started.</summary>
    private int _latest;

    /// <summary>Whether a move is under way, until it ends.</summary>
    public bool IsMoving { get; private set; }

    /// <summary>
    /// Start another move, once <see cref="Finish"/> has ended any under way. Returns its number, for
    /// <see cref="Completes"/>.
    /// </summary>
    public int Start()
    {
        if (IsMoving)
        {
            throw new InvalidOperationException("A move under way is finished before the next starts.");
        }

        IsMoving = true;

        return ++_latest;
    }

    /// <summary>
    /// Whether the animations of move <paramref name="run"/> running their course end the move under
    /// way: only where it is that move, and it has not been finished already.
    /// </summary>
    public bool Completes(int run) => IsMoving && run == _latest;

    /// <summary>
    /// End the move under way. True where one was, so its end is reported once, whether it ran its
    /// course or was finished early.
    /// </summary>
    public bool Finish()
    {
        if (!IsMoving)
        {
            return false;
        }

        IsMoving = false;

        return true;
    }
}
