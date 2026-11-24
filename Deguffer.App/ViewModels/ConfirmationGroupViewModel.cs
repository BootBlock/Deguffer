using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>One tier's heading in the clean's confirmation, its sentence where it has one, and its rows.</summary>
public sealed class ConfirmationGroupViewModel(CleanConfirmationGroup group, IReadOnlyList<ConfirmationRowViewModel> rows)
{
    public string Heading { get; } = group.Heading;

    public string Note { get; } = group.Note ?? string.Empty;

    public bool HasNote { get; } = group.Note is not null;

    public IReadOnlyList<ConfirmationRowViewModel> Rows { get; } = rows;
}
