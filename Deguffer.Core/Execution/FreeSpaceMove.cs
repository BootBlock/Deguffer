using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// §7: free space before and after a clean, from two readings of the volume, one either side of the run.
///
/// <para>Read from the volume, never worked out from what Deguffer removed (§5.4). The two disagree
/// whenever anything else writes during the run, or whenever a program gives its space back after the
/// run ends, and a figure derived from the other would hide exactly the difference §5.4 says to show.</para>
///
/// <para>The figure the user started from is stated, not only the change. A hero figure that is simply
/// replaced by the new free space leaves them to remember the old one.</para>
/// </summary>
/// <param name="Before">The volume's free space read before the run started.</param>
/// <param name="After">The volume's free space as read now.</param>
public sealed record FreeSpaceMove(long Before, long After)
{
    /// <summary>How far the free space moved, signed: less free space after than before is negative.</summary>
    public long Change => After - Before;

    /// <summary>The move in words, to stand beside the free space it describes.</summary>
    public string Text => Change switch
    {
        > 0 => $"Up {FreeSpace.Format(Change)} from {FreeSpace.Format(Before)} before the clean",
        < 0 => $"Down {FreeSpace.Format(-Change)} from {FreeSpace.Format(Before)} before the clean",
        _ => $"Unchanged from {FreeSpace.Format(Before)} before the clean",
    };

    /// <summary>
    /// The move as a sentence that stands on its own, away from the free space it describes, such as
    /// under a list the clean emptied. Says whose space it is, because nothing beside it does.
    /// </summary>
    public string Sentence => Change switch
    {
        > 0 => $"Free space is up {FreeSpace.Format(Change)} from {FreeSpace.Format(Before)} before the clean.",
        < 0 => $"Free space is down {FreeSpace.Format(-Change)} from {FreeSpace.Format(Before)} before the clean.",
        _ => $"Free space is unchanged at {FreeSpace.Format(Before)}.",
    };

    /// <summary>
    /// The move between two readings, or null where either is unknown. A figure from one reading alone
    /// would be a guess dressed as a measurement.
    /// </summary>
    public static FreeSpaceMove? Between(long? before, long? after) =>
        before is { } was && after is { } now ? new FreeSpaceMove(was, now) : null;
}
