using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One action within a finding, selectable on its own.
///
/// §4.3 calls for per-workspace folders to be prunable individually, and §7 makes age a first-class
/// column for exactly that data — "last touched 5 months ago" drives the decision more than size
/// does. Neither is expressible while a whole provider is the smallest thing a user can choose.
/// </summary>
public sealed partial class StepViewModel : ObservableObject
{
    public StepViewModel(CleanupStep step, bool isSelected)
    {
        Step = step;
        IsSelected = isSelected;
    }

    public CleanupStep Step { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Description => Step.Description;

    public string SizeLabel => FreeSpace.Format(Step.Estimated);

    /// <summary>
    /// §7's age column. Rendered as text rather than as a colour-coded indicator: §6.5 requires the
    /// classification to survive a flat background and a high-contrast theme, and an age carries the
    /// same weight here as the tier does.
    /// </summary>
    public string AgeLabel => RelativeAge.Describe(Step.LastWritten, DateTime.UtcNow);

    /// <summary>
    /// Whether this step has an age worth a column at all. Whole-cache steps do not, and showing
    /// "Unknown" against every npm row would be noise rather than information.
    /// </summary>
    public bool HasAge => Step.LastWritten is not null;

    /// <summary>
    /// Nothing to reclaim means nothing to choose, and neither does a step this process has no
    /// rights to carry out.
    ///
    /// Pairing the step's declaration with the token the app is actually running under happens here
    /// rather than in Core, because the declaration is a fact about the location and the token is a
    /// fact about this process. A plan that described the disk differently depending on who asked
    /// would be a worse thing to have than one line of conjunction in the shell.
    /// </summary>
    public bool CanBeSelected => Step.EstimatedBytes > 0 && !NeedsElevationFirst;

    /// <summary>
    /// Whether this step is one Deguffer can see and cannot remove as it is currently running.
    ///
    /// Shown beside the step, because the two alternatives are worse: a step that fails at execution
    /// time explains nothing, and a location dropped from an unelevated preview is a folder the user
    /// never learns about. "Elevate and rescan" is already on screen whenever this is true —
    /// <see cref="Deguffer.Core.Execution.ElevationOffer"/> reads the same claim.
    /// </summary>
    public bool NeedsElevationFirst => Step.RequiresElevation && !ElevatedRelaunch.IsElevated;

    /// <summary>
    /// Whether this step gets a checkbox of its own. Set by the owning row, because it is a fact
    /// about the set rather than about this step: where a finding has a single step, that step is
    /// the whole row and the row's own checkbox already decides it.
    ///
    /// A step template cannot reach its parent through <c>x:Bind</c>, which is why the answer is
    /// pushed down here rather than read back up.
    /// </summary>
    public bool IsIndividuallySelectable { get; init; }
}
