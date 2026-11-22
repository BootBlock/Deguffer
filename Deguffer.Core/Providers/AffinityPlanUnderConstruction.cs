using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the walk of both roots has accumulated so far. It exists because the walk is three levels
/// deep and every level can add a note, a protected path and a reason not to call the result
/// "already clear" — passing five collections and two counters down by reference is where one
/// level quietly stops contributing to one of them.
/// </summary>
internal sealed class AffinityPlanUnderConstruction
{
    public List<PlanNote> Notes { get; } = [];

    public List<DeletionTarget> Targets { get; } = [];

    public List<(string Path, string Reason)> Protect { get; } = [];

    /// <summary>Whether any folder refused to be listed, for <see cref="CleanupPlan.HasUnreadableRoot"/>.</summary>
    public bool Unreadable { get; set; }

    /// <summary>
    /// How many things Deguffer looked at and chose to leave. A plan with no steps and none of
    /// these examined an empty machine; one with no steps and some of these did not examine what
    /// it found, and §7 renders only the first as "already clear".
    /// </summary>
    public int Declined { get; set; }

    public void Decline(PlanNote note, (string Path, string Reason)? protect = null)
    {
        Notes.Add(note);
        Declined++;

        if (protect is { } path)
        {
            Protect.Add(path);
        }
    }

    /// <summary>
    /// A folder Windows would not list. It counts among <see cref="Declined"/> for the reason a
    /// link does: the plan under it is empty because nothing was read, not because there was
    /// nothing there, and the sentence saying Affinity has downloaded no models must not be said
    /// about it. Written once so the two levels that can meet a refusal cannot answer differently.
    /// </summary>
    public void Unread(string folder)
    {
        Notes.Add(UnreadableRoot.Note(folder));
        Unreadable = true;
        Declined++;
    }

    /// <summary>
    /// A folder Windows would not describe. Apart from <see cref="Unread"/>, whose sentence says
    /// Deguffer could not list the folder and so asserts that it is there.
    /// </summary>
    public void Unreached(string folder)
    {
        Notes.Add(UnreadableRoot.UnreachedNote(folder));
        Unreadable = true;
        Declined++;
    }
}
