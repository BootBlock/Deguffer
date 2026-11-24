using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What a Storage row's information dialog states on its Contents tab: the plan's notes, the one step
/// of a row with nothing to choose between, and whether there is anything to state at all.
///
/// <para>Apart from <see cref="FindingViewModel"/> because it moves only with the plan and the keep
/// list, never with a tick, so the row replaces it when either changes.</para>
/// </summary>
/// <param name="steps">Every step the provider planned, kept ones included.</param>
/// <param name="offersItems">Whether the row lists its steps as items on the Storage page.</param>
/// <param name="finding">The finding with the keep list applied, whose plan's notes are stated.</param>
public sealed class FindingContents(IReadOnlyList<StepViewModel> steps, bool offersItems, Finding finding)
{
    /// <summary>
    /// Materialised once per plan: these are bound, and building a list inside a getter puts an
    /// allocation on every binding evaluation.
    /// </summary>
    public IReadOnlyList<string> Notes { get; } = (string[])[.. finding.Plan?.Notes.Select(n => n.Message) ?? []];

    /// <summary>
    /// The one step of a row that has nothing to choose between, which the Contents tab still states
    /// in full: §7 makes the plan inspectable before anything is deleted. Null for a row whose steps
    /// are listed as items, and for a row with none.
    /// </summary>
    public StepViewModel? SoleStep { get; } = !offersItems && steps.Count == 1 ? steps[0] : null;

    public bool HasSoleStep => SoleStep is not null;

    /// <summary>
    /// Shown whenever there is anything to say — including for a tool with nothing to reclaim.
    /// A provider that decided to leave children alone under §5.2 has recorded *why*, and that
    /// reasoning is the audit trail; hiding it because the tool happens to be clean would throw
    /// away the most useful thing Deguffer knows about it.
    /// </summary>
    public bool HasDetail => steps.Count > 0 || Notes.Count > 0;

    /// <summary>
    /// Whether the Contents tab has to say that there is nothing to list. Stated here rather than
    /// negated in the template because x:Bind has no operators, and a converter for one "not" would
    /// be more machinery than the property it replaces.
    /// </summary>
    public bool HasNoDetail => !HasDetail;
}
