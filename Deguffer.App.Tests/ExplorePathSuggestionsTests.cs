using System.Collections.Specialized;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Network;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the list under the Path… box: one session from the box opening to its
/// closing, a lookup for text typed past stopped and shown nowhere, and the list brought up to date
/// in place. What is suggested is Core's (<c>NetworkSuggestionsTests</c>).
/// </summary>
public sealed class ExplorePathSuggestionsTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public ExplorePathSuggestionsTests()
    {
        _explore.Volumes.With(@"C:\");
        _explore.Network.ConnectedAnswer = NetworkListing<NetworkConnection>.Listed([new NetworkConnection(@"\\nas.test\media", "Z:")]);
        _explore.Network.Computer(
            "nas.test",
            new ListedShare("media", ShareKind.Disk, false),
            new ListedShare("Photos", ShareKind.Disk, false),
            new ListedShare("C$", ShareKind.Disk, true));
    }

    public void Dispose()
    {
        _explore.Network.Release("nas.test");
        _explore.Dispose();
    }

    [Fact]
    public void EachStageOfTypingListsItsOwnPlaces() => UiThread.Run(async () =>
    {
        var suggestions = Open();

        await suggestions.SuggestAsync(string.Empty);

        Assert.Equal([@"\\nas.test\media"], Paths(suggestions));
        Assert.Equal("Mapped drive, Z:", suggestions.Rows[0].Detail);

        await suggestions.SuggestAsync(@"\\nas.test\");

        Assert.Equal([@"\\nas.test\media", @"\\nas.test\Photos"], Paths(suggestions));
    });

    /// <summary>Nothing is asked of the network before the box opens, or after it closes.</summary>
    [Fact]
    public void NothingIsAskedOutsideTheBoxsSession() => UiThread.Run(async () =>
    {
        var suggestions = _explore.Page().Targeting.Typed.Suggestions;

        await suggestions.SuggestAsync(@"\\nas.test\");

        suggestions.Open();
        suggestions.Close();
        await suggestions.SuggestAsync(@"\\nas.test\");

        Assert.Empty(suggestions.Rows);
        Assert.Equal(0, _explore.Network.Asked("nas.test"));
    });

    /// <summary>
    /// What the network answered is kept for the session and asked again in the next one, so a
    /// computer renamed or a share added since shows the next time the box opens.
    /// </summary>
    [Fact]
    public void EachSessionAsksTheNetworkOnce() => UiThread.Run(async () =>
    {
        var suggestions = Open();

        await suggestions.SuggestAsync(@"\\nas.test\");
        await suggestions.SuggestAsync(@"\\nas.test\P");
        await suggestions.SuggestAsync(@"\\nas.test\");

        Assert.Equal(1, _explore.Network.Asked("nas.test"));

        suggestions.Close();
        suggestions.Open();
        await suggestions.SuggestAsync(@"\\nas.test\");

        Assert.Equal(2, _explore.Network.Asked("nas.test"));
    });

    /// <summary>
    /// A lookup for text the reader has typed past is stopped, and its answer is shown nowhere, even
    /// when it arrives after the newer one: the list shows what fits the text in the box.
    /// </summary>
    [Fact]
    public void ALookupTypedPastShowsNothing() => UiThread.Run(async () =>
    {
        var suggestions = Open();
        _explore.Network.Folder(@"\\files.test\team", "Reports");
        _explore.Network.Hold("nas.test");

        var stale = suggestions.SuggestAsync(@"\\nas.test\");
        await suggestions.SuggestAsync(@"\\files.test\team\");

        _explore.Network.Release("nas.test");
        await stale;

        Assert.Equal([@"\\files.test\team\Reports"], Paths(suggestions));
    });

    /// <summary>
    /// An answer that has already landed, and is queued for the UI thread as the reader types on, is
    /// dropped there: the list never shows, even for a moment, places for text no longer in the box.
    /// </summary>
    [Fact]
    public void AnAnswerQueuedAsTheReaderTypesOnIsNeverShown() => UiThread.Run(async () =>
    {
        var suggestions = Open();
        _explore.Network.Folder(@"\\files.test\team", "Reports");
        _explore.Network.Hold("nas.test");
        var shown = new List<string>();
        suggestions.Rows.CollectionChanged += (_, e) => shown.AddRange(e.NewItems?.OfType<ViewModels.PathSuggestionRow>().Select(row => row.Path) ?? []);

        var stale = suggestions.SuggestAsync(@"\\nas.test\");
        _explore.Network.Release("nas.test");

        // The UI thread is held while the answer lands on the pool, so its continuation is queued
        // behind the reader's next keystroke rather than run before it.
        Thread.Sleep(300);
        var current = suggestions.SuggestAsync(@"\\files.test\team\");

        await stale;
        await current;

        Assert.DoesNotContain(shown, path => path.StartsWith(@"\\nas.test", StringComparison.Ordinal));
        Assert.Equal([@"\\files.test\team\Reports"], Paths(suggestions));
    });

    /// <summary>The box closing stops a lookup still waiting, which then changes nothing on screen.</summary>
    [Fact]
    public void ClosingTheBoxStopsALookupAndChangesNothing() => UiThread.Run(async () =>
    {
        var suggestions = Open();
        await suggestions.SuggestAsync(string.Empty);
        _explore.Network.Hold("nas.test");

        var waiting = suggestions.SuggestAsync(@"\\nas.test\");

        // The share stage empties the list while the computer is asked, so the last stage's places are not offered under it.
        Assert.Empty(suggestions.Rows);

        suggestions.Close();
        _explore.Network.Release("nas.test");
        await waiting;

        Assert.Empty(suggestions.Rows);
    });

    /// <summary>A list brought up to date keeps the rows that stay, so one place more is one insertion, not a rebuilt list.</summary>
    [Fact]
    public void TheListIsUpdatedInPlace() => UiThread.Run(async () =>
    {
        var suggestions = Open();
        await suggestions.SuggestAsync(@"\\nas.test\");
        var changes = new List<NotifyCollectionChangedAction>();
        suggestions.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        await suggestions.SuggestAsync(@"\\nas.test\P");

        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
        Assert.Equal([@"\\nas.test\Photos"], Paths(suggestions));
    });

    /// <summary>A folder Explore has scanned is offered back while the box is empty, most recent first.</summary>
    [Fact]
    public void AFolderScannedIsOfferedBackAsRecent() => UiThread.Run(async () =>
    {
        var page = _explore.Page();
        _explore.Probe.Directory(@"\\?\UNC\nas.test\media\Photos");
        page.Targeting.Typed.Text = @"\\nas.test\media\Photos";
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        var builder = new ExploreTreeBuilder(@"\\nas.test\media\Photos");
        await _explore.ScanAsync(page, ExploreScan.Walked(builder.Build(ExploreChildOrder.BySize), Core.Scanning.FallbackReason.VolumeNotAddressable));

        var suggestions = page.Targeting.Typed.Suggestions;
        suggestions.Open();
        await suggestions.SuggestAsync(string.Empty);

        var recent = suggestions.Rows.Single(row => row.Suggestion.Group is NetworkGroup.Recent);
        Assert.Equal(@"\\nas.test\media\Photos", recent.Path);
        Assert.Equal([@"\\nas.test\media\Photos"], _explore.Preferences.Current.Locations.Recent);
    });

    /// <summary>A whole drive scanned is not kept as recent: the drive box already offers it.</summary>
    [Fact]
    public void AWholeDriveScannedIsNotKept() => UiThread.Run(async () =>
    {
        var page = _explore.Page();

        await _explore.ScanAsync(page, ExploreScan.Walked(new ExploreTreeBuilder(@"C:\").Build(ExploreChildOrder.BySize), Core.Scanning.FallbackReason.VolumeNotAddressable));

        Assert.Empty(_explore.Preferences.Current.Locations.Recent);
    });

    private ViewModels.ExplorePathSuggestions Open()
    {
        var suggestions = _explore.Page().Targeting.Typed.Suggestions;

        suggestions.Open();

        return suggestions;
    }

    private static string[] Paths(ViewModels.ExplorePathSuggestions suggestions) => [.. suggestions.Rows.Select(row => row.Path)];
}
