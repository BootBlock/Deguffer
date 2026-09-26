using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.App.Tests;

/// <summary>
/// What one row of the Explore list says. The wording is the shell's, and the rule behind most of
/// it is §7.1's: Explore's numbers may be lower bounds, and must say so, in the direction each one
/// can be wrong.
/// </summary>
public sealed class ExploreRowTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The bar is the row's share of the folder it is in, because the question the list answers is
    /// what that folder is made of. An empty folder has no shares, rather than a division by zero.
    /// </summary>
    [Fact]
    public void TheBarIsTheRowsShareOfItsFolder()
    {
        var (tree, big, small) = Files(750, 250);

        Assert.Equal(75, Row(tree, big).Share);
        Assert.Equal(25, Row(tree, small).Share);
        Assert.Equal(0, new ExploreRow(tree, small, parentTotal: 0, Now, _explore.Guide).Share);
    }

    /// <summary>
    /// A folder the walk could not read all of totals only what it could see. The figure is marked
    /// as the least it can be, because the row showing "0 B" is the one a reader acts on.
    /// </summary>
    [Fact]
    public void ASizeThatIsALowerBoundSaysSo()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var refused = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("System Volume Information"),
            ExploreFixture.File("pagefile.sys", 4096),
        ]);

        builder.MarkSizeUnknown(refused);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var row = Row(tree, refused);

        Assert.StartsWith("≥ ", row.SizeLabel);
        Assert.True(row.IsApproximate);
        Assert.EndsWith(", and some of this could not be read", row.Description);
        Assert.Equal(FreeSpace.Format(4096), Row(tree, refused + 1).SizeLabel);
    }

    /// <summary>
    /// The age behind a refusal can only be too old, never too new, so a known date is kept and
    /// qualified in that direction. An unknown one is already said in words, and a qualifier on
    /// "Unknown" would say nothing.
    /// </summary>
    [Fact]
    public void AnAgeThatMayBeTooOldSaysSo()
    {
        var written = ExploreTimestamp.FromUtc(Now.AddDays(-400));
        var builder = new ExploreTreeBuilder(@"C:\");
        var dated = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("dated", IsDirectory: true, IsLink: false, Size: 0, LastWritten: written),
            new ExploreChild("undated", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("readable", IsDirectory: true, IsLink: false, Size: 0, LastWritten: written),
        ]);

        builder.MarkSizeUnknown(dated);
        builder.MarkSizeUnknown(dated + 1);
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.EndsWith(" or newer", Row(tree, dated).AgeLabel);
        Assert.DoesNotContain("or newer", Row(tree, dated + 1).AgeLabel);
        Assert.DoesNotContain("or newer", Row(tree, dated + 2).AgeLabel);
    }

    /// <summary>
    /// A folder's second date is the newest write anywhere inside it, not its own timestamp, and the
    /// label says so, so it is not compared with the figure Explorer shows for something else.
    /// </summary>
    [Fact]
    public void AFoldersDateIsNamedForWhatItMeasures()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("Projects"),
            ExploreFixture.File("notes.txt", 10),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Contains("Newest write inside: ", Row(tree, folder).DatesLabel);
        Assert.Contains("Last written: ", Row(tree, folder + 1).DatesLabel);
        Assert.Contains("Created: not known", Row(tree, folder + 1).DatesLabel);
    }

    /// <summary>
    /// The tooltip explains what a thing is where Deguffer knows, and gives the dates alone where it
    /// does not: one appearing over everything with nothing to add teaches the reader to ignore it.
    /// </summary>
    [Fact]
    public void TheTooltipExplainsOnlyWhatDegufferKnows()
    {
        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\src\app");
        var known = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("node_modules"),
            ExploreFixture.Folder("src"),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var explained = Row(tree, known);
        var plain = Row(tree, known + 1);

        Assert.Contains("JavaScript project", explained.Tip);
        Assert.Contains(explained.DatesLabel, explained.Tip);
        Assert.Equal(plain.DatesLabel, plain.Tip);
    }

    /// <summary>
    /// A later tree rewrites the same row rather than replacing it, which is what lets a snapshot
    /// landing mid-scan leave the list where the reader had it.
    /// </summary>
    [Fact]
    public void ALaterTreeRewritesTheRowItHas()
    {
        var (first, node, _) = Files(100, 100);
        var (later, _, _) = Files(300, 100);
        var row = Row(first, node);
        var changed = new List<string?>();

        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Describe(later, later.SizeOf(later.RootNode), Now);

        Assert.Equal(75, row.Share);
        Assert.Equal(FreeSpace.Format(300), row.SizeLabel);
        Assert.Contains(nameof(ExploreRow.SizeLabel), changed);
        Assert.Equal(ExploreRow.KeyOf(later, node), row.Key);
    }

    private ExploreRow Row(ExploreTree tree, int node) =>
        new(tree, node, tree.SizeOf(tree.ParentOf(node)), Now, _explore.Guide);

    private static (ExploreTree Tree, int First, int Second) Files(long first, long second)
    {
        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\Downloads");
        var node = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.File("first.bin", first),
            ExploreFixture.File("second.bin", second),
        ]);

        return (builder.Build(ExploreChildOrder.BySize), node, node + 1);
    }
}
