using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One group as the page lists it: what matched, what its copies that could go occupy, the copies,
/// and why a rule left it as it was. Built once, as the group arrives, and never rebuilt.
/// </summary>
public sealed partial class DuplicateGroupRow : ObservableObject
{
    /// <param name="keeping">The keeping rule the marks were made under, which says what the group could free.</param>
    /// <param name="toggle">Marks or unmarks a copy, answering Core's refusal, or null where it was done.</param>
    /// <param name="mayMark">Whether the page lets any mark change now.</param>
    public DuplicateGroupRow(GroupMarks marks, CopyKeeping keeping, Func<DuplicateCopyRow, string?> toggle, Func<bool> mayMark)
    {
        Marks = marks;

        var group = marks.Group;
        var paths = PathDifference.Of([.. group.Files.Select(copy => copy.Path)]);

        Copies = [.. group.Files.Select((copy, i) => new DuplicateCopyRow(copy, paths[i], marks, keeping, toggle, mayMark))];
        Title = group.Length is { } length
            ? $"{group.Files.Count:N0} files of {FreeSpace.Format(length)}"
            : $"{group.Files.Count:N0} files";
        Freeable = $"Copies that could go occupy {FreeSpace.Format(marks.FreeableSpace(keeping))}";
        Matched = group.Checksum is { } checksum
            ? $"Matched on {group.Criteria.Described()}: {checksum.Algorithm.Name()} {checksum.Hex}"
            : $"Matched on {group.Criteria.Described()}";
        MayDiffer = group.MayDiffer ?? string.Empty;
        Note = marks.WhyNothingCanBeKept(keeping) ?? string.Empty;
    }

    public GroupMarks Marks { get; }

    public IReadOnlyList<DuplicateCopyRow> Copies { get; }

    public string Title { get; }

    /// <summary>
    /// What the copies that could be removed occupy, with one copy that can be kept left, which is
    /// what the list is sorted by and which a removal may free less than.
    /// </summary>
    public string Freeable { get; }

    /// <summary>The criteria, and the checksum with its algorithm where the content was compared.</summary>
    public string Matched { get; }

    /// <summary>The group's sentence that its files may differ, or an empty string for a content match.</summary>
    public string MayDiffer { get; }

    public bool HasMayDiffer => MayDiffer.Length > 0;

    /// <summary>
    /// Why no copy here can be kept, or why the last rule left the group as it was, or an empty
    /// string.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial string Note { get; private set; }

    public bool HasNote => Note.Length > 0;

    /// <summary>Everything the group's heading shows, in one sentence, for a screen reader.</summary>
    public string Description =>
        $"{Title}. {Freeable}. {Matched}."
        + (MayDiffer.Length > 0 ? " " + MayDiffer : string.Empty)
        + (Note.Length > 0 ? " " + Note : string.Empty);

    /// <summary>Say why the last rule left the group as it was, or, where it did not, why no copy here can be kept.</summary>
    internal void Noted(string? note) => Note = note ?? string.Empty;
}
