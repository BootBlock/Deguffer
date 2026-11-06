using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One group as the page lists it: which file it is, what matched, what its copies that could go
/// occupy, how many are marked, the copies, and why a rule left it as it was. Built once, as the
/// group arrives, and never rebuilt.
/// </summary>
public sealed partial class DuplicateGroupRow : ObservableObject
{
    /// <param name="judged">
    /// The group as the search judged it by the keeping rule the marks were made under, which says
    /// what it could free and what each copy's row lists, so building the row asks the rule nothing.
    /// </param>
    /// <param name="toggle">Marks or unmarks a copy, answering Core's refusal, or null where it was done.</param>
    /// <param name="mayMark">Whether the page lets any mark change now.</param>
    public DuplicateGroupRow(GroupMarks marks, GroupJudgement judged, Func<DuplicateCopyRow, string?> toggle, Func<bool> mayMark)
    {
        Marks = marks;

        var group = marks.Group;
        var paths = PathDifference.Of([.. group.Files.Select(copy => copy.Path)]);

        Copies = [.. group.Files.Select((copy, i) => new DuplicateCopyRow(copy, paths[i], this, judged.Standings[i], toggle, mayMark))];
        Name = NameOf(group);
        Glyph = FileKindGlyph.Of(group.Files[0].Path);
        Summary = group.Length is { } length
            ? $"{group.Files.Count:N0} copies of {FreeSpace.Format(length)}, matched on {group.Criteria.Described()}"
            : $"{group.Files.Count:N0} copies, matched on {group.Criteria.Described()}";
        Checksum = group.Checksum is { } checksum ? $"{checksum.Algorithm.Name()} {checksum.Hex}" : string.Empty;
        FreeableSpace = judged.FreeableSpace;
        Freeable = FreeSpace.Format(FreeableSpace);
        MayDiffer = group.MayDiffer ?? string.Empty;
        Note = judged.WhyNothingCanBeKept ?? string.Empty;
    }

    public GroupMarks Marks { get; }

    public IReadOnlyList<DuplicateCopyRow> Copies { get; }

    /// <summary>
    /// The file's name, which is how a reader knows the group, or the first copy's name and how many
    /// others there are where a match on the content or the size joined files under different names.
    /// </summary>
    public string Name { get; }

    /// <summary>The glyph for what kind of file the group is (<see cref="FileKindGlyph"/>).</summary>
    public string Glyph { get; }

    /// <summary>How many copies there are, of what size, and the criteria that matched them.</summary>
    public string Summary { get; }

    /// <summary>The checksum with its algorithm where the content was compared, or an empty string.</summary>
    public string Checksum { get; }

    public bool HasChecksum => Checksum.Length > 0;

    /// <summary>
    /// What the copies that could be removed occupy, with one copy that can be kept left, which is
    /// what the list is sorted by and which a removal may free less than.
    /// </summary>
    public string Freeable { get; }

    /// <summary>What <see cref="Freeable"/> says, which the page's figures add up.</summary>
    internal long FreeableSpace { get; }

    /// <summary>The group's sentence that its files may differ, or an empty string for a content match.</summary>
    public string MayDiffer { get; }

    public bool HasMayDiffer => MayDiffer.Length > 0;

    /// <summary>How many of the copies are marked, in words, or an empty string where none is.</summary>
    public string MarkedCount => Marks.MarkedCount == 0 ? string.Empty : $"{Marks.MarkedCount:N0} of {Copies.Count:N0} marked";

    public bool HasMarks => Marks.MarkedCount > 0;

    /// <summary>
    /// What the marks that stand here occupy, as last counted, which the page's marked total adds up
    /// without asking the policy of every copy in every group each time one mark changes.
    /// </summary>
    internal long MarkedSpace { get; private set; }

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
    public string Description => string.Join(
        " ",
        new[]
        {
            $"{Name}. {Summary}. Copies that could go occupy {Freeable}.",
            Checksum.Length > 0 ? $"Checksum {Checksum}." : string.Empty,
            MayDiffer,
            MarkedCount.Length > 0 ? MarkedCount + "." : string.Empty,
            Note,
        }.Where(sentence => sentence.Length > 0));

    /// <summary>Say why the last rule left the group as it was, or, where it did not, why no copy here can be kept.</summary>
    internal void Noted(string? note) => Note = note ?? string.Empty;

    /// <summary>
    /// Count the group's marks under <paramref name="keeping"/> and show them, after one copy's mark
    /// changed.
    /// </summary>
    internal void Recount(CopyKeeping keeping) => Counted(CountMarked(keeping));

    /// <summary>
    /// What the group's standing marks occupy under <paramref name="keeping"/>, asking the policy of
    /// no copy where nothing is marked. It only reads, so it may be asked off the page's thread.
    /// </summary>
    internal long CountMarked(CopyKeeping keeping) => Marks.MarkedCount > 0 ? Marks.MarkedSpace(keeping) : 0;

    /// <summary>Show the group's marks, with the space they occupy counted already, after one copy's mark changed.</summary>
    internal void Counted(long markedSpace)
    {
        MarkedSpace = markedSpace;
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(Description));

        foreach (var copy in Copies)
        {
            copy.MarksShown();
        }
    }

    /// <summary>
    /// Show the group's marks, with the space they occupy counted already, after a rule or a clear
    /// changed them, dropping each copy's refusal that no longer answers anything.
    /// </summary>
    internal void MarksChanged(long markedSpace)
    {
        foreach (var copy in Copies)
        {
            copy.ClearNote();
        }

        Counted(markedSpace);
    }

    private static string NameOf(DuplicateGroup group)
    {
        var first = Path.GetFileName(group.Files[0].Path);

        // Ignoring case as Windows does by default: two copies named IMG.JPG and img.jpg are one name
        // to a reader. A case-sensitive folder can hold both, and each row still shows its own path.
        var others = group.Files
            .Select(copy => Path.GetFileName(copy.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() - 1;

        return others switch
        {
            0 => first,
            1 => $"{first} and 1 other name",
            _ => $"{first} and {others:N0} other names",
        };
    }
}
