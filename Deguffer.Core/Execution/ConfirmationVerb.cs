namespace Deguffer.Core.Execution;

/// <summary>
/// The word a confirmation uses for what the user is authorising, so the title and the button say
/// one word for one action, and the word carries the weight of the loss.
/// </summary>
public enum ConfirmationVerb
{
    /// <summary>
    /// The user's action wherever nothing is lost for good: Tier 1, and Tier 2, whose cost is time
    /// rather than data.
    /// </summary>
    Clean,

    /// <summary>
    /// Kept for a permanent loss alone (Tier 3), so the word carries weight where it appears. Said of a
    /// Tier 2 row too, it would say nothing more on the rows where everything is lost.
    /// </summary>
    Delete,

    /// <summary>
    /// A plan that only asks a sync app to release local copies of cloud files. "Delete" over a plan
    /// that destroys nothing is a false statement about the user's cloud files, and the one a reader
    /// is most likely to refuse a harmless clean over.
    /// </summary>
    Release,
}

public static class ConfirmationVerbExtensions
{
    /// <summary>The word as the user reads it, at the start of a title or a button.</summary>
    public static string ToWord(this ConfirmationVerb verb) => verb switch
    {
        ConfirmationVerb.Delete => "Delete",
        ConfirmationVerb.Release => "Release",
        _ => "Clean",
    };
}
