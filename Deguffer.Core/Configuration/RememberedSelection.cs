namespace Deguffer.Core.Configuration;

/// <summary>
/// What one provider's row on the Storage page was left ticked as, so the next scan can start
/// where the user left off rather than back at the tier defaults.
///
/// Both levels are recorded because the row is only a roll-up of its steps. Remembering the row
/// alone would restore a ticked row by ticking every step in it, which silently re-selects the
/// per-workspace folders the user had picked out and left alone — and that is the one direction a
/// mistake here must never go.
/// </summary>
/// <param name="IsSelected">Whether the row itself was ticked.</param>
/// <param name="Steps">
/// Each step of that row, keyed by <see cref="Execution.CleanupStep.SelectionKey"/>. Holds the
/// steps of the scan this was written from, so it shrinks and grows with the machine rather than
/// accumulating every path Deguffer has ever planned. A step the map does not mention is one this
/// entry knows nothing about, and it starts at whatever the row says.
/// </param>
public sealed record RememberedSelection(bool IsSelected, IReadOnlyDictionary<string, bool> Steps)
{
    /// <summary>
    /// What to record about a row, from its steps as they stand.
    /// </summary>
    /// <param name="steps">
    /// Each step's key, whether it is ticked, and whether the user could have ticked it.
    /// </param>
    /// <remarks>
    /// A step the run disabled is left out rather than recorded as unticked. It is unticked because
    /// Deguffer unticked it — it has nothing to reclaim yet, or it needs administrator rights this
    /// process does not have — and writing that down as the user's answer carries it into the run
    /// where the step finally can be acted on. A Tier 1 provider is enough to show it: a recognised
    /// child that happens to be empty is a nought-byte step today, and once the tool writes into it
    /// the user's ticked row would quietly exclude it, with nothing on screen to say so.
    ///
    /// Left out, it has no answer, and <see cref="SelectionMemory.StepStartsSelected"/> starts it
    /// from the row instead.
    /// </remarks>
    public static RememberedSelection Of(
        bool isSelected,
        IEnumerable<(string Key, bool Selected, bool CanBeSelected)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        Dictionary<string, bool> recorded = [];

        foreach (var (key, selected, canBeSelected) in steps)
        {
            if (canBeSelected)
            {
                recorded[key] = selected;
            }
        }

        return new RememberedSelection(isSelected, recorded);
    }
}
