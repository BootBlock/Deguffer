using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.4: "the page names what it left out, so a search that skipped a place is never read as one
/// that found nothing there". A folder Windows would not list and a location read from a file table
/// that was not read whole are named, and so is the route each location was read by.
/// </summary>
public sealed class DuplicateUnreadPlacesTests : IDisposable
{
    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// The refused folder holds the second copy, so a search that read it as empty finds nothing,
    /// which is exactly the answer that must not be given in silence.
    /// </summary>
    [Fact]
    public async Task AFolderWindowsWillNotListIsNamedAsUnread()
    {
        _tree.File(100, "Data", "a.jpg");
        _tree.File(100, "Data", "Locked", "b.jpg");
        var locked = Path.Combine(_tree.Top, "Data", "Locked");
        using var denied = new DeniedDirectory(locked);

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(Path.Combine(_tree.Top, "Data")));

        Assert.Empty(found.Groups);
        var unread = Assert.Single(found.Unread);
        Assert.Equal(locked, unread.Path);
        Assert.Contains("would not let Deguffer list this folder", unread.Reason, StringComparison.Ordinal);
    }

    /// <summary>A place passed over is named once, for being passed over, whatever is refused inside it.</summary>
    [Fact]
    public async Task AFolderWindowsWillNotListInsideAPlacePassedOverIsNotNamedAgain()
    {
        _tree.File(100, "Windows", "Locked", "a.dll");
        var locked = Path.Combine(_tree.System.WindowsDirectory, "Locked");
        using var denied = new DeniedDirectory(locked);

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Top));

        Assert.Contains(found.PassedOver, place => place.Path == _tree.System.WindowsDirectory);
        Assert.Empty(found.Unread);
    }

    public enum Damage
    {
        None,
        TableStopsPartWay,
        ARecordCannotBeRead,
    }

    /// <summary>
    /// A record the table read could not place might have been anywhere, this location included, so
    /// the location is named as one that may not have been searched in full. What was read is still
    /// searched.
    /// </summary>
    [Theory]
    [InlineData(Damage.None)]
    [InlineData(Damage.TableStopsPartWay)]
    [InlineData(Damage.ARecordCannotBeRead)]
    public async Task ALocationReadFromAnIncompleteTableIsNamedAsUnread(Damage damage)
    {
        var data = MftRecord.ReservedRecordCount;
        var fixture = new MftFixture()
            .AddDirectory(data, MftRecord.RootRecordNumber, "Data")
            .AddFile(data + 1, data, "a.jpg", allocated: 4096, logical: 100)
            .AddFile(data + 2, data, "b.jpg", allocated: 4096, logical: 100)
            .AddFile(data + 3, data, "c.jpg", allocated: 4096, logical: 100);

        _ = damage switch
        {
            Damage.TableStopsPartWay => fixture.UnreadableFrom(data + 3),
            Damage.ARecordCannotBeRead => fixture.CorruptSectorStamp(data + 3),
            _ => fixture,
        };

        var scan = Assert.Single(await new ExploreScanner(FakeMftSourceFactory.Serving('X', fixture), tuning: RouteTuners.Table)
            .ScanFoldersAsync([@"X:\Data"]));
        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);

        var location = new ResolvedLocation(
            new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", new FakeVolumeInventory()), LocationRole.Search,
            new LocalVolume(@"X:\", DriveType.Fixed, VolumeReadiness.Ready));
        var walk = new CandidateWalk(new DuplicateSearch(MatchCriteria.Size, [location.Given]), new UnresolvedReferences([]));
        walk.Read(scan, location, [], below: null, IdentityRoute.FileId, default);

        Assert.Equal(damage == Damage.None ? 3 : 2, Assert.Single(CandidateGrouping.ByTheTree(walk.Found, MatchCriteria.Size, CandidateGrouping.TreeMinuteOf)).Count);

        if (damage == Damage.None)
        {
            Assert.Empty(walk.Unread);
        }
        else
        {
            var unread = Assert.Single(walk.Unread);
            Assert.Equal(@"X:\Data", unread.Path);
            Assert.Contains("file table could not be read", unread.Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// §5.5 makes the route observable. A search read by walking because the table was not to be had
    /// says so for the location, as Explore says so beside its picture.
    /// </summary>
    [Fact]
    public async Task EachLocationSaysTheRouteItWasReadBy()
    {
        var data = _tree.Folder("Data");

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(data));

        var read = Assert.Single(found.Read);
        Assert.Equal(data, read.Folder);
        Assert.NotNull(read.RouteNote);
        Assert.Equal(ExploreRouteText.Describe(ScanStrategy.ParallelEnumeration, FallbackReason.NotElevated), read.RouteNote);
    }
}
