using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore list is brought to a later snapshot of the folder on screen. While a scan runs
/// every snapshot reaches every row, and nearly none of them has changed.
/// </summary>
public sealed class ExploreRowsTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// #680: a snapshot in which the folder on screen did not change keeps every row and words no
    /// label. The culture changes between the two, so a label worded again would read in the new
    /// culture's format, and a row that raised anything would say so.
    /// </summary>
    [Fact]
    public void ASnapshotThatLeavesTheFolderAsItWasRaisesNothingOnAnyRow()
    {
        using var culture = new CultureSwap("en-GB");
        var clock = new ManualTimeProvider();
        var listing = new ExploreRows(_explore.Guide, clock) { IsActive = true };
        var first = Folder();

        listing.Show(first, ExplorePosition.Top(first), VolumeSpace.None, reconcile: false);

        var rows = listing.Rows.ToList();
        var labels = rows.Select(Labels).ToList();
        var raised = new List<string?>();

        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) => raised.Add($"{row.Name}.{e.PropertyName}");
        }

        culture.To("de-DE");
        clock.Advance(TimeSpan.FromMinutes(1));

        var later = Folder();

        // The case under test: a culture that words these rows apart.
        var fresh = new ExploreRows(_explore.Guide, clock) { IsActive = true };
        fresh.Show(later, ExplorePosition.Top(later), VolumeSpace.None, reconcile: false);
        Assert.All(fresh.Rows.Zip(labels), pair => Assert.NotEqual(pair.Second, Labels(pair.First)));

        listing.Show(later, ExplorePosition.Top(later), VolumeSpace.None, reconcile: true);

        Assert.Empty(raised);
        Assert.Equal(rows, listing.Rows);
        Assert.Equal(labels, listing.Rows.Select(Labels));
    }

    private static (string Size, string Dates) Labels(ExploreRow row) => (row.SizeLabel, row.DatesLabel);

    /// <summary>A folder of dated files whose sizes are worded with a decimal.</summary>
    private static ExploreTree Folder()
    {
        var written = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\Downloads");

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Written("setup.exe", 3 * 1024 * 1024 / 2, written),
            ExploreFixture.Written("notes.iso", 5 * 1024 * 1024 / 2, written.AddDays(1)),
            ExploreFixture.Written("film.mkv", 7 * 1024 * 1024 / 2, written.AddDays(2)),
        ]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
