using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// When a later tree leaves a node's labels as they were. A row that wrongly takes a changed node
/// for an unchanged one shows a stale size or age for as long as the folder is on screen, so every
/// figure a label is worded from has to tell two readings apart, and nothing else may.
/// </summary>
public sealed class ExploreNodeFiguresTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Written = Now.AddDays(-10).AddHours(3);

    /// <summary>
    /// A file-table snapshot numbers its nodes afresh, so the same entry under another number, in
    /// another tree, is the same reading.
    /// </summary>
    [Fact]
    public void TheSameEntryUnderAnotherNumberReadsAlike()
    {
        var (first, node) = Single(Entry());

        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\Downloads");
        var moved = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("earlier.bin", IsDirectory: false, IsLink: false, Size: 1),
            Entry(),
        ]) + 1;
        var later = builder.Build(ExploreChildOrder.BySize);

        Assert.NotEqual(node, moved);
        Assert.Equal(ExploreNodeFigures.Of(first, node, Now), ExploreNodeFigures.Of(later, moved, Now));
    }

    public static TheoryData<string, ExploreChild> Changes => new()
    {
        { "size", Entry() with { Size = 4097 } },
        { "length", Entry() with { Length = 9000 } },
        { "storage", Entry() with { Storage = FileStorage.Compressed } },
        { "created", Entry() with { Created = ExploreTimestamp.FromUtc(Written.AddDays(-1)) } },
        { "modified", Entry() with { LastWritten = ExploreTimestamp.FromUtc(Written.AddMinutes(5)) } },
        { "unknown modified", Entry() with { LastWritten = ExploreTimestamp.Unknown } },
        { "lower bound", Entry() with { SizeUnknown = true } },
    };

    /// <summary>Every figure a label is worded from tells two readings apart.</summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void AChangeToAnyWordedFigureReadsDifferently(string figure, ExploreChild changed)
    {
        var (before, node) = Single(Entry());
        var (after, _) = Single(changed);

        Assert.True(
            ExploreNodeFigures.Of(before, node, Now) != ExploreNodeFigures.Of(after, node, Now),
            $"A change to the {figure} read as unchanged.");
    }

    /// <summary>
    /// The age is worded from whole days, so a later instant within the same day reads alike, and
    /// the first one past the next whole day reads differently though nothing in the tree moved.
    /// </summary>
    [Fact]
    public void TheAgeReadsDifferentlyOnlyOnceItsDayTurnsOver()
    {
        var (tree, node) = Single(Entry());
        var figures = ExploreNodeFigures.Of(tree, node, Now);

        Assert.Equal(figures, ExploreNodeFigures.Of(tree, node, Now.AddHours(2)));
        Assert.NotEqual(figures, ExploreNodeFigures.Of(tree, node, Now.AddHours(4)));
        Assert.Equal(9, figures.AgeInDays);
    }

    /// <summary>
    /// The day count is the one <see cref="RelativeAge.Describe"/> words, so a timestamp in the
    /// future counts as today, and an absent one as no count at all rather than as an age.
    /// </summary>
    [Fact]
    public void TheDayCountIsTheOneTheAgeIsWordedFrom()
    {
        Assert.Null(RelativeAge.DaysSince(null, Now));
        Assert.Equal(0, RelativeAge.DaysSince(Now.AddDays(3), Now));
        Assert.Equal("Today", RelativeAge.Describe(Now.AddDays(3), Now));
        Assert.Equal(150, RelativeAge.DaysSince(Now.AddDays(-150), Now));
        Assert.Equal("5 months ago", RelativeAge.Describe(Now.AddDays(-150), Now));
    }

    private static ExploreChild Entry() => new(
        "setup.exe",
        IsDirectory: false,
        IsLink: false,
        Size: 4096,
        Created: ExploreTimestamp.FromUtc(Written),
        LastWritten: ExploreTimestamp.FromUtc(Written),
        Length: 4000);

    private static (ExploreTree Tree, int Node) Single(ExploreChild child)
    {
        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\Downloads");
        var node = builder.AddChildren(ExploreTreeBuilder.RootNode, [child]);

        return (builder.Build(ExploreChildOrder.BySize), node);
    }
}
