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
    /// <param name="preSelect">
    /// What §3's "Default" column says for the owning row. It is honoured only where this step can
    /// actually be acted on, so the caller states the row's intent and the answer to "may this be
    /// ticked?" stays in one place — see <see cref="CanBeSelected"/>.
    /// </param>
    /// <param name="isKept">
    /// Whether the keep list took this step out of the row's plan. Set before the tick, because a kept
    /// item is never pre-selected, whatever the row says.
    /// </param>
    public StepViewModel(CleanupStep step, bool preSelect, bool isKept)
    {
        Step = step;
        IsKept = isKept;
        IsSelected = preSelect && CanBeSelected;
    }

    public CleanupStep Step { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Whether this item is on the user's keep list. Observable, because the user keeps and releases
    /// it from the dialog it is listed in, and the checkbox beside it has to follow.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeSelected))]
    [NotifyPropertyChangedFor(nameof(KeepActionLabel))]
    [NotifyPropertyChangedFor(nameof(KeepActionName))]
    public partial bool IsKept { get; set; }

    /// <summary>What the item is apart from its path, where its provider can say. See <see cref="ItemIdentity"/>.</summary>
    public ItemIdentity? Identity => (Step as DeleteStep)?.Identity;

    /// <summary>Whether this item can go on the keep list at all, which needs an identity to match it by.</summary>
    public bool CanBeKept => Identity is not null;

    /// <summary>
    /// Whether the keep button may be pressed now. Set by the page, which refuses a change to the
    /// keep list while a preview or a clean is running.
    /// </summary>
    [ObservableProperty]
    public partial bool CanChangeKeepList { get; set; }

    public string KeepActionLabel => IsKept ? "Stop keeping" : "Keep";

    /// <summary>
    /// What a screen reader calls the keep button. Every row's button reads "Keep", so without the item
    /// in its name a reader hears the same word down the whole list.
    /// </summary>
    public string KeepActionName => $"{KeepActionLabel} {Description}";

    public string Description => Step.Description;

    public string SizeLabel => FreeSpace.Format(Step.Reclaim);

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
    /// rights to carry out, nor an item the user keeps.
    ///
    /// Pairing the step's declaration with the token the app is actually running under happens here
    /// rather than in Core, because the declaration is a fact about the location and the token is a
    /// fact about this process. A plan that described the disk differently depending on who asked
    /// would be a worse thing to have than one line of conjunction in the shell.
    ///
    /// A kept item is not in the row's plan at all, so a tick on it would count towards the selected
    /// total and remove nothing. Refusing it here is what the row-wide toggle and the roll-up read.
    ///
    /// "Nothing to reclaim" is <see cref="CleanupStep.RemovesSomething"/>'s answer rather than a byte
    /// test, so a leftover of empty folders can be chosen and an empty cache folder its tool re-creates
    /// still cannot.
    /// </summary>
    public bool CanBeSelected => Step.RemovesSomething && !NeedsElevationFirst && !IsKept;

    /// <summary>
    /// Whether this step is one Deguffer can see and cannot remove as it is currently running.
    ///
    /// Shown beside the step, because the two alternatives are worse: a step that fails at execution
    /// time explains nothing, and a location dropped from an unelevated preview is a folder the user
    /// never learns about. The Elevate button is already on screen whenever this is true —
    /// <see cref="Deguffer.Core.Execution.ElevationOffer"/> reads the same claim.
    /// </summary>
    public bool NeedsElevationFirst => Step.RequiresElevation && !ElevatedRelaunch.IsElevated;

    /// <summary>
    /// This step's value under each of its row's facet columns, in order, with an empty string where it
    /// has none. Set by the owning row, which works the columns out once across every step. See
    /// <see cref="Deguffer.Core.Choosing.ItemColumns"/>.
    /// </summary>
    public IReadOnlyList<string> FacetValues { get; init; } = [];
}
