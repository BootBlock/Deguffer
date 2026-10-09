using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.4: each location is resolved to where it is before anything is enumerated, so a file reached by
/// two names is searched once, and a finder never lists a file as its own duplicate.
///
/// <para><b>The proof is a count.</b> Each folder below holds two files of one length, and a size
/// search groups every file of a length together. Searched once, the folder makes one group of two;
/// searched through both of its names it makes one group of four, and not searched at all it makes
/// none.</para>
/// </summary>
public sealed class SearchLocationsTests : IDisposable
{
    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private void TwoFiles(params string[] folder)
    {
        _tree.File(100, [.. folder, "a.jpg"]);
        _tree.File(100, [.. folder, "b.jpg"]);
    }

    private static void SearchedOnce(CandidateFinding found) =>
        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);

    [Fact]
    public async Task TwoDifferentFoldersAreBothSearched()
    {
        TwoFiles("Photos");
        TwoFiles("Backup");

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(_tree.Top, "Photos")),
            new SearchLocation(Path.Combine(_tree.Top, "Backup")));

        Assert.Equal(4, Assert.Single(found.Groups).Files.Count);
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task AFolderReachedThroughALinkIsSearchedOnce(DirectoryLinkKind kind)
    {
        TwoFiles("Photos");
        DirectoryLink.Create(kind, Path.Combine(_tree.Top, "Shortcut"), Path.Combine(_tree.Top, "Photos"));

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(_tree.Top, "Photos")),
            new SearchLocation(Path.Combine(_tree.Top, "Shortcut")));

        SearchedOnce(found);
    }

    /// <summary>The link need not be the location's own name: one in the middle of its path leads there too.</summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void AFolderReachedThroughALinkHigherUpItsPathIsSearchedOnce(DirectoryLinkKind kind)
    {
        _tree.File(100, "Data", "Photos", "a.jpg");
        DirectoryLink.Create(kind, Path.Combine(_tree.Top, "Shortcut"), Path.Combine(_tree.Top, "Data"));

        var locations = SearchLocations.Resolve(
            [new(Path.Combine(_tree.Top, "Data")), new(Path.Combine(_tree.Top, "Shortcut", "Photos"))],
            _tree.Volumes);

        // The second is inside the first once both are where they really are, so it is read as part of it.
        Assert.Equal(Path.Combine(_tree.Top, "Data"), Assert.Single(locations.Roots).Folder);
        Assert.Contains(locations.Locations, location => location.Folder == Path.Combine(_tree.Top, "Data", "Photos"));
    }

    [Fact]
    public async Task AFolderReachedThroughASubstitutedDriveIsSearchedOnce()
    {
        TwoFiles("Photos");

        // A letter nothing on this machine uses, so only the inventory's substitution can lead
        // anywhere: a real drive at the letter would be opened, and searched, in its place.
        var letter = Enumerable.Range('D', 23).Select(c => $"{(char)c}:{Path.DirectorySeparatorChar}")
            .Last(root => !Directory.Exists(root));
        _tree.Volumes.Substituting(letter, Path.Combine(_tree.Top, "Photos"));

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(_tree.Top, "Photos")),
            new SearchLocation(letter));

        SearchedOnce(found);
        Assert.Empty(found.Unsearched);
    }

    /// <summary>
    /// One volume mounted at two folders. The two folders here are real and distinct on the disk,
    /// because a test cannot mount a volume; what makes them one is the inventory saying so, which is
    /// all the search may go by, and each holds a file so that searching both would make a group.
    /// </summary>
    [Fact]
    public async Task AFolderReachedThroughTwoMountsOfOneVolumeIsSearchedOnce()
    {
        var volume = _tree.Folder("Volume") + Path.DirectorySeparatorChar;
        var mount = _tree.Folder("Mount") + Path.DirectorySeparatorChar;
        _tree.Volumes.With(volume, alsoMountedAt: [mount]);
        TwoFiles("Volume", "Photos");
        TwoFiles("Mount", "Photos");

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(volume, "Photos")),
            new SearchLocation(Path.Combine(mount, "Photos")));

        SearchedOnce(found);
    }

    [Fact]
    public async Task AFolderNamedTwiceIsSearchedOnce()
    {
        TwoFiles("Photos");
        var photos = Path.Combine(_tree.Top, "Photos");

        var found = await _tree.FindAsync(
            MatchCriteria.Size, new SearchLocation(photos), new SearchLocation(photos + Path.DirectorySeparatorChar));

        SearchedOnce(found);
    }

    [Fact]
    public async Task AFolderInsideAnotherLocationIsSearchedOnce()
    {
        TwoFiles("Pictures", "Holiday");

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(_tree.Top, "Pictures", "Holiday")),
            new SearchLocation(Path.Combine(_tree.Top, "Pictures")));

        SearchedOnce(found);
    }

    /// <summary>A case-sensitive folder can hold <c>Photos</c> and <c>photos</c>, which are two folders.</summary>
    [Fact]
    public void FoldersWhosePathsDifferOnlyInCaseAreComparedAsWindowsNamesThem()
    {
        var drive = new LocalVolume(@"C:\", DriveType.Fixed, VolumeReadiness.Ready);

        Assert.False(SearchLocations.Holds(Resolved(@"C:\Data\Photos", drive), Resolved(@"C:\Data\photos\a", drive)));
        Assert.True(SearchLocations.Holds(Resolved(@"C:\Data\Photos", drive), Resolved(@"C:\Data\Photos\a", drive)));
        Assert.True(SearchLocations.Holds(Resolved(@"C:\", drive), Resolved(@"C:\Data", drive)));
    }

    /// <summary>
    /// A volume with no letter is named by the folder it is mounted at, so its folders read as inside
    /// the drive holding that folder. The walk of that drive never crosses the mount point, so a
    /// location held by its text alone would be searched by nothing.
    ///
    /// <para>Asked of the resolution rather than of a search: a test cannot mount a volume, so the
    /// mount point here is a plain folder the inventory calls a volume, and a walk would enter it.</para>
    /// </summary>
    [Fact]
    public void ALocationOnAVolumeMountedInsideAnotherIsSearchedInItsOwnRight()
    {
        _tree.Volumes.With(_tree.Folder("Mount") + Path.DirectorySeparatorChar);
        var photos = _tree.Folder("Mount", "Photos");

        var locations = SearchLocations.Resolve(
            [new(_tree.Top), new(photos, LocationRole.Reference)], _tree.Volumes);

        Assert.Equal(2, locations.Roots.Count);
        var top = locations.Roots.Single(location => location.Folder == _tree.Top);
        var inner = locations.Roots.Single(location => location.Folder == photos);
        Assert.Equal(LocationRole.Reference, inner.Role);
        Assert.Empty(locations.Within(top));
    }

    /// <summary>The same two folders on one volume: the inner one is enumerated as part of the outer.</summary>
    [Fact]
    public void ALocationInsideAnotherOnOneVolumeIsHeldByIt()
    {
        var photos = _tree.Folder("Mount", "Photos");

        var locations = SearchLocations.Resolve(
            [new(_tree.Top), new(photos, LocationRole.Reference)], _tree.Volumes);

        var top = Assert.Single(locations.Roots);
        Assert.Equal(photos, Assert.Single(locations.Within(top)).Folder);
    }

    private static ResolvedLocation Resolved(string folder, LocalVolume volume) =>
        new(new(folder), folder, ReachedFolder.At(folder, new FakeVolumeInventory()), LocationRole.Search, volume);

    [Fact]
    public void ANetworkShareIsNotSearched()
    {
        var locations = SearchLocations.Resolve([new(@"\\fileserver.test\photos")], _tree.Volumes);

        Assert.Empty(locations.Roots);
        Assert.Contains("network share", Assert.Single(locations.Unsearched).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriveWindowsCallsANetworkDriveIsNotSearched()
    {
        var drive = _tree.Folder("Mapped") + Path.DirectorySeparatorChar;
        _tree.Volumes.With(drive, DriveType.Network);

        var locations = SearchLocations.Resolve([new(drive)], _tree.Volumes);

        Assert.Empty(locations.Roots);
        Assert.Contains("network share", Assert.Single(locations.Unsearched).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A letter mapped to a share (<c>net use Z:</c>), where opening anything is a conversation with
    /// another machine, and a drive that keeps its files in the cloud, whose driver answers the open:
    /// each is refused on what the inventory says of it, before anything is opened.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Network, VolumeFeatures.ReparsePoints, "network share")]
    [InlineData(DriveType.Fixed, VolumeFeatures.RemoteStorage, "cloud")]
    public void ADriveTheInventoryRefusesIsNeverOpened(DriveType kind, VolumeFeatures features, string reason)
    {
        var letter = Enumerable.Range('D', 23).Select(c => $"{(char)c}:{Path.DirectorySeparatorChar}")
            .Last(root => !Directory.Exists(root));
        _tree.Volumes.With(letter, kind, features: features);
        List<string> opened = [];
        var files = new FileInformation(path =>
        {
            opened.Add(path);
            return FileInformation.OpenToResolve(path);
        });

        var locations = SearchLocations.Resolve([new(Path.Combine(letter, "Photos"))], _tree.Volumes, files);

        Assert.Empty(locations.Roots);
        Assert.Contains(reason, Assert.Single(locations.Unsearched).Reason, StringComparison.Ordinal);
        Assert.Empty(opened);
    }

    /// <summary>
    /// A link on a local disk can lead to a drive the search refuses, which the path as named does not
    /// show, so the drive is asked again where the link leads.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Network, VolumeFeatures.ReparsePoints, "network share")]
    [InlineData(DriveType.Fixed, VolumeFeatures.RemoteStorage, "cloud")]
    public void AFolderALinkLeadsToIsJudgedByTheDriveItIsOn(DriveType kind, VolumeFeatures features, string reason)
    {
        _tree.Volumes.With(_tree.Folder("Elsewhere") + Path.DirectorySeparatorChar, kind, features: features);
        _tree.Folder("Elsewhere", "Photos");
        Junction.ToDirectory(Path.Combine(_tree.Top, "Link"), Path.Combine(_tree.Top, "Elsewhere", "Photos"));

        var locations = SearchLocations.Resolve([new(Path.Combine(_tree.Top, "Link"))], _tree.Volumes);

        Assert.Empty(locations.Roots);
        Assert.Contains(reason, Assert.Single(locations.Unsearched).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cloud client's drive that Windows reports as a fixed disk: its files carry no sign that
    /// reading them downloads them, so the drive is what is refused.
    /// </summary>
    [Fact]
    public void ADriveThatKeepsItsFilesInTheCloudIsNotSearched()
    {
        var drive = _tree.Folder("Cloud") + Path.DirectorySeparatorChar;
        _tree.Volumes.With(drive, DriveType.Fixed, features: VolumeFeatures.RemoteStorage);

        var locations = SearchLocations.Resolve([new(drive)], _tree.Volumes);

        Assert.Empty(locations.Roots);
        Assert.Contains("cloud", Assert.Single(locations.Unsearched).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocationWindowsWillNotOpenIsNotSearchedAndIsNotCalledAbsent()
    {
        var locations = SearchLocations.Resolve([new(Path.Combine(_tree.Top, "Nowhere"))], _tree.Volumes);

        var unsearched = Assert.Single(locations.Unsearched);
        Assert.Empty(locations.Roots);
        Assert.Contains("Windows would not open this folder", unsearched.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not there", unsearched.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileIsNotALocation()
    {
        var file = _tree.File(10, "a.txt");

        Assert.Single(SearchLocations.Resolve([new(file)], _tree.Volumes).Unsearched);
    }
}
