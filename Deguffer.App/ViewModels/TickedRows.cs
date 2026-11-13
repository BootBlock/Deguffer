using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The rows ticked when Clean read them, and the steps ticked in each, read on the UI thread, which is
/// the only thread that may read a tick.
///
/// <para><b>Asked again once the narrowing is done</b> (<see cref="AreCurrent"/>). The narrowing awaits
/// the disk, and the checkboxes stay live while it does. A step the user unticks in that time is on
/// screen as left alone, so a run of the plans narrowed from the earlier ticks would delete it, and
/// §5.6 would not check it, because it was never a declined subject. Clean narrows again from the
/// ticks as they then stand.</para>
/// </summary>
internal sealed class TickedRows
{
    private readonly Row[] _rows;

    private TickedRows(Row[] rows) => _rows = rows;

    /// <summary>
    /// What is ticked in <paramref name="findings"/> now. A row with no plan offers no step, so it is
    /// never ticked and never read.
    /// </summary>
    public static TickedRows Of(IEnumerable<FindingViewModel> findings)
    {
        List<Row> rows = [];

        foreach (var view in findings)
        {
            if (view.IsSelected && view.Finding is { Plan: { } plan } finding)
            {
                rows.Add(new Row(view, finding, plan, [.. view.SelectedSteps]));
            }
        }

        return new([.. rows]);
    }

    /// <summary>Each row's plan with the steps ticked in it, in the rows' order.</summary>
    public IReadOnlyList<ChosenSteps> Choices =>
        [.. _rows.Select(row => new ChosenSteps(row.Plan, [.. row.Ticked.Select(step => step.Step)]))];

    /// <summary>
    /// Whether <paramref name="findings"/> still show these ticks: the same rows ticked, each still
    /// showing the finding it was read with, and the same steps ticked in each.
    /// </summary>
    public bool AreCurrent(IEnumerable<FindingViewModel> findings)
    {
        var now = Of(findings)._rows;

        return now.Length == _rows.Length
            && now.Zip(_rows).All(pair =>
                ReferenceEquals(pair.First.View, pair.Second.View)
                && ReferenceEquals(pair.First.Finding, pair.Second.Finding)
                && pair.First.Ticked.SequenceEqual(pair.Second.Ticked, ReferenceEqualityComparer.Instance));
    }

    /// <summary>Each row's finding with the plan <paramref name="narrowed"/> gives it, in the rows' order.</summary>
    /// <param name="narrowed">What the narrowing returned for <see cref="Choices"/>, in their order.</param>
    public List<Finding> With(IReadOnlyList<CleanupPlan> narrowed) =>
        [.. _rows.Select((row, index) => row.Finding with { Plan = narrowed[index] })];

    private readonly record struct Row(FindingViewModel View, Finding Finding, CleanupPlan Plan, StepViewModel[] Ticked);
}
