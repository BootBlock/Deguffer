using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One copy in a group: where it is, cut where its path differs from the others', what it is, whether
/// it is marked, and why it may not be marked or counted on as the copy kept.
/// </summary>
public sealed partial class DuplicateCopyRow : ObservableObject
{
    private readonly Func<DuplicateCopyRow, string?> _toggle;
    private readonly Func<bool> _mayMark;

    /// <param name="group">The marks on the copy's group, which say whether it is marked.</param>
    /// <param name="keeping">The keeping rule as last judged, which says why it may not be marked or kept.</param>
    /// <param name="toggle">Marks or unmarks the copy, answering Core's refusal, or null where it was done.</param>
    /// <param name="mayMark">Whether the page lets any mark change now.</param>
    public DuplicateCopyRow(
        DuplicateCandidate copy,
        PathParts path,
        GroupMarks group,
        CopyKeeping keeping,
        Func<DuplicateCopyRow, string?> toggle,
        Func<bool> mayMark)
    {
        Copy = copy;
        Group = group;
        _toggle = toggle;
        _mayMark = mayMark;
        Same = path.Same;
        Differs = path.Differs;
        SameEnd = path.SameEnd;
        Size = FreeSpace.Format(copy.Length);
        Modified = copy.Modified.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        Names = copy.HasSeveralNames
            ? $"One file with {copy.NameCount:N0} names, so removing one name frees nothing: {string.Join("; ", copy.Names)}"
            : string.Empty;
        Show(CopyStanding.Of(copy, keeping));
    }

    public DuplicateCandidate Copy { get; }

    public GroupMarks Group { get; }

    /// <summary>The start of the path, which another copy's path starts with too.</summary>
    public string Same { get; }

    /// <summary>What no other copy's path shares at its start or end, which the row picks out.</summary>
    public string Differs { get; }

    /// <summary>The end of the path, which another copy's path ends with too.</summary>
    public string SameEnd { get; }

    public string Size { get; }

    public string Modified { get; }

    public bool IsReference => Copy.Role == LocationRole.Reference;

    public bool HasSeveralNames => Copy.HasSeveralNames;

    /// <summary>Every name of a file with several, or an empty string, because a binding cannot show null.</summary>
    public string Names { get; }

    /// <summary>Whether the copy is marked, as its group's marks say.</summary>
    public bool IsMarked => Group.IsMarked(Copy);

    /// <summary>Why the copy is never marked or is refused, as last judged, or an empty string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(Reasons))]
    [NotifyPropertyChangedFor(nameof(HasReasons))]
    public partial string WhyNotMarked { get; private set; } = string.Empty;

    /// <summary>Why the copy cannot be the one its group keeps, where that is not why it is not marked, or an empty string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(Reasons))]
    [NotifyPropertyChangedFor(nameof(HasReasons))]
    public partial string WhyNotKept { get; private set; } = string.Empty;

    /// <summary>Both reasons, a line each, as the row shows them under the path.</summary>
    public string Reasons => string.Join(Environment.NewLine, new[] { WhyNotMarked, WhyNotKept }.Where(reason => reason.Length > 0));

    public bool HasReasons => WhyNotMarked.Length > 0 || WhyNotKept.Length > 0;

    /// <summary>Why the last mark asked of it was refused, or what a removal did with it, or an empty string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; private set; } = string.Empty;

    public bool HasNote => Note.Length > 0;

    /// <summary>Everything the row shows, in one sentence, for a screen reader.</summary>
    public string Description =>
        $"{Copy.Path}, {Size}, last modified {Modified}"
        + (IsReference ? ", in a reference location" : string.Empty)
        + (Copy.HasSeveralNames ? $", one file with {Copy.NameCount:N0} names" : string.Empty)
        + Sentence(WhyNotMarked) + Sentence(WhyNotKept) + Sentence(Note);

    /// <summary>The check box's name for a screen reader, which says which copy it marks.</summary>
    public string MarkName => $"Mark {Copy.Path} for removal";

    /// <summary>
    /// Mark the copy, or unmark it, as Core allows. A refusal is shown on the row and the check box
    /// is told the copy's state again, so it never shows a mark Core refused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle()
    {
        Note = _toggle(this) ?? string.Empty;
        OnPropertyChanged(nameof(IsMarked));
    }

    private bool CanToggle() => _mayMark();

    /// <summary>Show what Core judged of the copy.</summary>
    internal void Show(CopyStanding standing)
    {
        WhyNotMarked = standing.WhyNotMarked;
        WhyNotKept = standing.WhyNotKept;
    }

    /// <summary>Show the copy's mark again, after a rule or a clear changed the marks, dropping a refusal it no longer answers.</summary>
    internal void MarksChanged()
    {
        Note = string.Empty;
        OnPropertyChanged(nameof(IsMarked));
    }

    /// <summary>Show what a removal did with the copy.</summary>
    internal void Removal(string message) => Note = message;

    /// <summary>Whether the page lets marks change has changed.</summary>
    internal void GateChanged() => ToggleCommand.NotifyCanExecuteChanged();

    private static string Sentence(string text) => text.Length == 0 ? string.Empty : ". " + text;
}
