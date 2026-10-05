using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A finished plan told which of the paths it measured the walk could not reach: each is named, and
/// the plan says it holds a location Windows would not let Deguffer read.
///
/// <para><b>Why the plan and not the step.</b> A path that was not reached measures zero, and a step
/// with nothing to reclaim is never offered, so the step already does the safe thing and needs
/// nothing more. What it cannot do is explain itself. Left alone, a row whose only cache was refused
/// reads "Already clear", which is a claim about a folder nobody saw into — so the plan carries
/// <see cref="CleanupPlan.HasUnreadableRoot"/> and the sentence naming the folder.</para>
///
/// <para>Applied to every plan by <see cref="CleanupProviderBase.PlanAsync"/>, for the reason
/// <see cref="MailStorePlan"/> is: every measurement a provider makes passes through that class, and
/// a fix each provider had to copy onto its own plan is the one some provider forgets.</para>
/// </summary>
internal static class UnmeasuredPaths
{
    /// <param name="plan">The plan a provider built.</param>
    /// <param name="unreached">Every path its measurements did not reach, and how.</param>
    public static CleanupPlan Apply(CleanupPlan plan, IReadOnlyCollection<(string Path, RootReach Root)> unreached)
    {
        if (unreached.Count == 0)
        {
            return plan;
        }

        // Sorted, because the measurements behind one plan can run in parallel and the notes would
        // otherwise change order between two previews of the same machine. A note the provider
        // already wrote about the same folder is not repeated.
        var notes = unreached
            .DistinctBy(path => path.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Path, StringComparer.OrdinalIgnoreCase)
            .Select(path => NoteFor(LongPath.Display(path.Path), path.Root))
            .Where(note => !plan.Notes.Contains(note));

        return plan with { Notes = [.. plan.Notes, .. notes], HasUnreadableRoot = true };
    }

    private static PlanNote NoteFor(string path, RootReach root) => root switch
    {
        RootReach.NotListed => UnreadableRoot.Note(path),
        RootReach.NotDescribed => UnreadableRoot.UnmeasuredNote(path),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "A reached path has nothing to say."),
    };
}
