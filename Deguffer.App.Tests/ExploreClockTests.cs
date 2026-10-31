using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Tests;

/// <summary>
/// Every age the Explore page writes out is measured against the clock it was given, so the folder
/// list, the line under the map and the Files layout agree with one another, and a test can pin each
/// one.
/// </summary>
public sealed class ExploreClockTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// The page's clock is a century ahead of the machine's, and the file was written three days
    /// before it. Measured against the machine's clock, the write is in the future and reads "Today",
    /// so neither age can pass by agreeing with the machine.
    /// </summary>
    [Fact]
    public void TheFolderListAndTheLineUnderTheMapDateAnAgeAgainstThePagesClock() => UiThread.Run(async () =>
    {
        _explore.Time.Advance(TimeSpan.FromDays(365 * 100));

        var written = _explore.Time.GetUtcNow().UtcDateTime.AddDays(-3);
        var root = _explore.Temp.CreateDirectory("scan");
        var builder = new ExploreTreeBuilder(root);

        builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Written("notes.txt", 10, written),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var page = _explore.Page();

        page.Targeting.ScopeTo(root);
        await _explore.ScanAsync(page, ExploreScan.Fast(tree));

        var row = Assert.Single(page.Listing.Rows);
        Assert.Equal("3 days ago", row.AgeLabel);

        page.Hover(new ExploreHit(row.Node, 10));
        Assert.EndsWith("last written 3 days ago", page.Readout.Figures);
    });
}
