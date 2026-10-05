namespace Deguffer.Core.Scanning;

/// <summary>
/// Whether a measurement reached the path it was asked about, and in which way it did not.
///
/// <para>It exists because a zero is otherwise ambiguous, and every reader of a zero makes a claim
/// from it: the preview says "Already clear", and the executor subtracts it from the figure before a
/// run and reports the difference as reclaimed. Neither claim is true of a path Windows would not let
/// the walk into, which may hold the largest tree on the machine. Only the measurement knows which
/// happened, so it is the measurement that says, as <see cref="ScanResult.WithheldRecent"/> does.
/// </para>
///
/// <para><b>Only the path itself, never a folder inside it.</b> A folder refused below the root
/// contributes nothing, and that is the correct figure rather than a gap: every figure a scanner
/// gives is what a removal can take, and a removal running as the same account is refused the same
/// listing, so it cannot empty that folder either. The root is different because nothing at all was
/// measured, and a figure of nothing reads as a claim that there is nothing.</para>
///
/// <para>The two unreached shapes are kept apart because they establish different things, as
/// <see cref="Providers.UnreadableRoot"/> explains: a root that would not be listed is certainly
/// there, and one Windows would not describe is not even known to exist.</para>
/// </summary>
public enum RootReach
{
    /// <summary>
    /// The path was read: walked, sized as a file, or answered absent. An absent path measures zero
    /// and that is a complete answer, because there is nothing there to hold anything.
    /// </summary>
    Reached,

    /// <summary>
    /// Windows describes the directory and would not let the walk list it, so it is there and
    /// nothing in it was measured.
    /// </summary>
    NotListed,

    /// <summary>
    /// Windows would not say what is at the path at all: an access rule, or a link it declines to
    /// follow. Nothing establishes that anything is there, or that nothing is.
    /// </summary>
    NotDescribed,
}
