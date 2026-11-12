using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One copy in a group: where it is, cut where its path differs from the others', what it is, whether
/// it is marked or stays, and why it may not be marked or counted on as the copy kept.
/// </summary>
public sealed partial class DuplicateCopyRow : ObservableObject
{
    private readonly Func<DuplicateCopyRow, string?> _toggle;
    private readonly Func<bool> _mayMark;

    /// <param name="owner">The copy's group, whose marks say whether it is marked.</param>
    /// <param name="standing">What the keeping rule judged of the copy: why it may not be marked or kept.</param>
    /// <param name="toggle">Marks or unmarks the copy, answering Core's refusal, or null where it was done.</param>
    /// <param name="mayMark">Whether the page lets any mark change now.</param>
    public DuplicateCopyRow(
        DuplicateCandidate copy,
        PathParts path,
        DuplicateGroupRow owner,
        CopyStanding standing,
        Func<DuplicateCopyRow, string?> toggle,
        Func<bool> mayMark)
    {
        Copy = copy;
        Owner = owner;
        _toggle = toggle;
        _mayMark = mayMark;
        Same = path.Same;
        Differs = path.Differs;
        SameEnd = path.SameEnd;
        Size = FreeSpace.Format(copy.Length);
        Modified = copy.Modified.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        Names = copy.HasSeveralNames
            ? $"Its names: {string.Join("; ", copy.Names)}."
            : string.Empty;
        Show(standing);
    }

    public DuplicateCandidate Copy { get; }

    /// <summary>The group the copy is listed in, which shows its marks again when one of them changes.</summary>
    public DuplicateGroupRow Owner { get; }

    public GroupMarks Group => Owner.Marks;

    /// <summary>Whether the copy is its group's last, whose row closes the group's card.</summary>
    public bool IsLast => ReferenceEquals(Owner[^1], this);

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

    /// <summary>
    /// Every name of a file with several, as a sentence the row shows under the path (§7.4: such a
    /// file is listed with every one of its names), or an empty string, because a binding cannot show null.
    /// </summary>
    public string Names { get; }

    /// <summary>Whether the copy is marked, as its group's marks say.</summary>
    public bool IsMarked => Group.IsMarked(Copy);

    /// <summary>
    /// Whether the row says the copy stays: it is unmarked in a group where another copy is marked.
    /// An unmarked copy is never removed, so this holds whatever the keeping rule says of it.
    /// </summary>
    public bool Stays => !IsMarked && Group.MarkedCount > 0;

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

    /// <summary>A file's names, then both reasons, a line each, as the row shows them under the path.</summary>
    public string Reasons => string.Join(Environment.NewLine, new[] { Names, WhyNotMarked, WhyNotKept }.Where(line => line.Length > 0));

    public bool HasReasons => Names.Length > 0 || WhyNotMarked.Length > 0 || WhyNotKept.Length > 0;

    /// <summary>Why the last mark asked of it was refused, or what a removal did with it, or an empty string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; private set; } = string.Empty;

    public bool HasNote => Note.Length > 0;

    /// <summary>
    /// Everything the row shows, for a screen reader: what the copy is and whether it is marked or
    /// stays, then each sentence under it once, so a refusal that repeats why the copy is never
    /// marked is not read twice.
    /// </summary>
    public string Description => string.Join(
        " ",
        new[]
        {
            $"{Copy.Path}, {Size}, last modified {Modified}{(IsReference ? ", in a reference location" : string.Empty)}.",
            IsMarked ? "Marked for removal." : Stays ? "Stays." : string.Empty,
            Names,
            WhyNotMarked,
            WhyNotKept,
            Note,
        }.Where(sentence => sentence.Length > 0).Distinct(StringComparer.Ordinal));

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

        // Told again even where the group showed its marks: a refusal before any mark changed shows
        // nothing there, and the check box has already drawn the mark it was clicked to make.
        MarksShown();
    }

    private bool CanToggle() => _mayMark();

    /// <summary>Show what Core judged of the copy.</summary>
    internal void Show(CopyStanding standing)
    {
        WhyNotMarked = standing.WhyNotMarked ?? string.Empty;
        WhyNotKept = standing.WhyNotKept ?? string.Empty;
    }

    /// <summary>Drop the refusal or removal the row says, after a rule or a clear changed the marks it answered.</summary>
    internal void ClearNote() => Note = string.Empty;

    /// <summary>Show the copy's mark, and whether it stays, as its group's marks say now.</summary>
    internal void MarksShown()
    {
        OnPropertyChanged(nameof(IsMarked));
        OnPropertyChanged(nameof(Stays));
        OnPropertyChanged(nameof(Description));
    }

    /// <summary>Show what a removal did with the copy.</summary>
    internal void Removal(string message) => Note = message;

    /// <summary>Whether the page lets marks change has changed.</summary>
    internal void GateChanged() => ToggleCommand.NotifyCanExecuteChanged();
}
