using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which files a search keeps, what role each has, and how they are grouped before anything is read
/// (§7.4).
/// </summary>
public sealed class DuplicateCandidateTests : IDisposable
{
    /// <summary>The drive of the trees these tests build by hand, which no test reads from.</summary>
    private static readonly LocalVolume DriveX = new(@"X:\", DriveType.Fixed, VolumeReadiness.Ready);

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private SearchLocation Searched(params string[] segments) => new(Path.Combine([_tree.Top, .. segments]));

    private static IEnumerable<string> Names(CandidateGroup group) => group.Files.Select(file => file.Name).Order();

    private static IEnumerable<string> Names(IReadOnlyList<FoundFile> group) => group.Select(file => file.Tree.NameOf(file.Node)).Order();

    /// <summary>Opens each path as Windows does, except <paramref name="refused"/>, spelled exactly so, which Windows will not open.</summary>
    private static FileInformation Refusing(string refused) => new(
        (path, use) => LongPath.Display(path).Equals(refused, StringComparison.Ordinal)
            ? new SafeFileHandle(-1, ownsHandle: false)
            : FileInformation.Open(path, use),
        FileInformation.ReadIdentity);

    [Fact]
    public async Task AReferenceInsideASearchedFolderStaysAReference()
    {
        var copy = _tree.File(100, "Data", "Downloads", "a.jpg");
        var kept = _tree.File(100, "Data", "Photos", "a.jpg");

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            Searched("Data"),
            new SearchLocation(Path.Combine(_tree.Top, "Data", "Photos"), LocationRole.Reference));

        var files = Assert.Single(found.Groups).Files;
        Assert.Equal(LocationRole.Search, files.Single(file => file.Path == copy).Role);
        Assert.Equal(LocationRole.Reference, files.Single(file => file.Path == kept).Role);
    }

    /// <summary>The innermost location decides, so a searched folder inside a reference is searched.</summary>
    [Fact]
    public async Task ASearchedFolderInsideAReferenceIsSearched()
    {
        var kept = _tree.File(100, "Photos", "a.jpg");
        var copy = _tree.File(100, "Photos", "Imported", "a.jpg");

        var found = await _tree.FindAsync(
            MatchCriteria.Size,
            new SearchLocation(Path.Combine(_tree.Top, "Photos"), LocationRole.Reference),
            Searched("Photos", "Imported"));

        var files = Assert.Single(found.Groups).Files;
        Assert.Equal(LocationRole.Reference, files.Single(file => file.Path == kept).Role);
        Assert.Equal(LocationRole.Search, files.Single(file => file.Path == copy).Role);
    }

    [Theory]
    [InlineData(LocationRole.Search, LocationRole.Reference)]
    [InlineData(LocationRole.Reference, LocationRole.Search)]
    public async Task AFolderGivenInBothRolesIsAReference(LocationRole first, LocationRole second)
    {
        _tree.File(100, "Photos", "a.jpg");
        _tree.File(100, "Photos", "b.jpg");
        var photos = Path.Combine(_tree.Top, "Photos");

        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(photos, first), new SearchLocation(photos, second));

        Assert.All(Assert.Single(found.Groups).Files, file => Assert.Equal(LocationRole.Reference, file.Role));
    }

    /// <summary>
    /// A reference Windows would not open names no folder the walk can be told is it, so its files
    /// would take the role of the location holding them and be offered for removal. The place it
    /// names is passed over with everything in it, a folder inside it included, and the page says why.
    /// Searching the places a search passes over by default does not lift it: that choice lets a
    /// refused copy be matched, and this place is passed over to keep a reference copy unmarked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReferenceWindowsWillNotOpenIsPassedOverByTheLocationHoldingIt(bool searchPassedOverPlaces)
    {
        _tree.File(100, "Downloads", "a.jpg");
        _tree.File(100, "Downloads", "b.jpg");
        _tree.File(100, "Photos", "a.jpg");
        _tree.File(100, "Photos", "Album", "c.jpg");
        var photos = Path.Combine(_tree.Top, "Photos");

        var found = await _tree.Finder(files: Refusing(photos)).FindAsync(
            new DuplicateSearch(
                MatchCriteria.Size,
                [Searched(), new SearchLocation(photos, LocationRole.Reference)],
                searchPassedOverPlaces: searchPassedOverPlaces),
            _tree.Policy());

        Assert.Equal(photos, Assert.Single(found.Unsearched).Given.Path);
        Assert.Contains("reference", Assert.Single(found.PassedOver, place => place.Path == photos).Reason, StringComparison.Ordinal);
        var group = Assert.Single(found.Groups);
        Assert.Equal(["a.jpg", "b.jpg"], Names(group));
        Assert.All(group.Files, file => Assert.Equal(Path.Combine(_tree.Top, "Downloads"), Path.GetDirectoryName(file.Path)));
    }

    /// <summary>
    /// A folder given in both roles is a reference, so where only its reference could not be opened,
    /// the folder is passed over rather than searched in the role that was resolved. The two are
    /// told apart here by the case they are spelled in, which the place is matched ignoring. As
    /// below a location, searching every place a search passes over by default does not lift it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFolderGivenInBothRolesIsPassedOverWhereItsReferenceWillNotOpen(bool searchPassedOverPlaces)
    {
        _tree.File(100, "Photos", "a.jpg");
        _tree.File(100, "Photos", "b.jpg");
        var photos = Path.Combine(_tree.Top, "Photos");
        var shouted = Path.Combine(_tree.Top, "PHOTOS");

        var found = await _tree.Finder(files: Refusing(shouted)).FindAsync(
            new DuplicateSearch(
                MatchCriteria.Size,
                [new SearchLocation(photos), new SearchLocation(shouted, LocationRole.Reference)],
                searchPassedOverPlaces: searchPassedOverPlaces),
            _tree.Policy());

        Assert.Equal(shouted, Assert.Single(found.Unsearched).Given.Path);
        Assert.Contains("reference", Assert.Single(found.PassedOver, place => place.Path == photos).Reason, StringComparison.Ordinal);
        Assert.Empty(found.Groups);
    }

    /// <summary>
    /// A location to search that Windows would not open asks for nothing a location holding it does
    /// not already do, so its files are searched there in the role they would have had.
    /// </summary>
    [Fact]
    public async Task ASearchedFolderWindowsWillNotOpenIsSearchedByTheLocationHoldingIt()
    {
        _tree.File(100, "Downloads", "a.jpg");
        _tree.File(100, "Photos", "a.jpg");
        var photos = Path.Combine(_tree.Top, "Photos");

        var found = await _tree.Finder(files: Refusing(photos)).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [Searched(), new SearchLocation(photos)]),
            _tree.Policy());

        Assert.Equal(photos, Assert.Single(found.Unsearched).Given.Path);
        Assert.DoesNotContain(found.PassedOver, place => place.Path == photos);
        var group = Assert.Single(found.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, file => Assert.Equal(LocationRole.Search, file.Role));
    }

    /// <summary>
    /// A folder matching a location only when case is ignored can be another folder in a
    /// case-sensitive directory, so it takes the safer role. The tree is built by hand, because a test
    /// cannot rely on making a case-sensitive directory.
    /// </summary>
    [Fact]
    public void AFolderMatchingAReferenceOnlyIgnoringCaseIsAReference()
    {
        var builder = new ExploreTreeBuilder(@"X:\Data");
        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [new("photos", IsDirectory: true, IsLink: false, Size: 0)]);
        builder.AddChildren(first, [new("a.jpg", IsDirectory: false, IsLink: false, Size: 100)]);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [new("b.jpg", IsDirectory: false, IsLink: false, Size: 100)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var volumes = new FakeVolumeInventory();
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", volumes), LocationRole.Search, DriveX);
        var reference = new ResolvedLocation(
            new(@"X:\Data\Photos", LocationRole.Reference), @"X:\Data\Photos", ReachedFolder.At(@"X:\Data\Photos", volumes),
            LocationRole.Reference, DriveX);

        var walk = new CandidateWalk(new DuplicateSearch(MatchCriteria.Size, [root.Given]));
        walk.Read(Scanned(tree, tree.RootNode), root, [reference], new PassedOverBelow(new UnresolvedReferences([]), below: null), IdentityRoute.FileId, default);

        var group = Assert.Single(CandidateGrouping.ByTheTree(walk.Found, MatchCriteria.Size, CandidateGrouping.TreeMinuteOf));
        Assert.Equal(LocationRole.Reference, group.Single(file => tree.NameOf(file.Node) == "a.jpg").Role);
        Assert.Equal(LocationRole.Search, group.Single(file => tree.NameOf(file.Node) == "b.jpg").Role);
    }

    [Fact]
    public async Task TheSizeFilterKeepsBothOfItsBoundsAndNothingPastThem()
    {
        foreach (var length in new[] { 9, 10, 20, 21 })
        {
            _tree.File(length, "One", $"{length}.bin");
            _tree.File(length, "Two", $"{length}.bin");
        }

        var found = await _tree.FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [Searched()], sizes: new SizeRange(10, 20)));

        Assert.Equal([10L, 20L], found.Groups.Select(group => group.Length!.Value).Order());
    }

    [Fact]
    public async Task OnlyTheListedExtensionsAreSearchedWhateverTheirCase()
    {
        _tree.File(10, "One", "a.jpg");
        _tree.File(10, "Two", "b.JPG");
        _tree.File(20, "One", "c.png");
        _tree.File(20, "Two", "d.png");
        _tree.File(30, "One", "e");
        _tree.File(30, "Two", "f");

        // Neither is a jpg: one ends in another extension after it, and the other's extension only
        // ends in the letters.
        _tree.File(10, "One", "g.jpg.bak");
        _tree.File(10, "Two", "h.xjpg");

        var found = await _tree.FindAsync(new DuplicateSearch(
            MatchCriteria.Size, [Searched()], extensions: new ExtensionFilter(ExtensionFilterMode.OnlyThese, ["JPG"])));

        Assert.Equal(["a.jpg", "b.JPG"], Names(Assert.Single(found.Groups)));
    }

    [Fact]
    public async Task TheSkippedExtensionsAreAllThatIsLeftOut()
    {
        _tree.File(10, "One", "a.jpg");
        _tree.File(10, "Two", "b.JPG");
        _tree.File(20, "One", "c.png");
        _tree.File(20, "Two", "d.png");
        _tree.File(30, "One", "e");
        _tree.File(30, "Two", "f");
        _tree.File(40, "One", "g.jpg.bak");
        _tree.File(40, "Two", "h.xjpg");

        var found = await _tree.FindAsync(new DuplicateSearch(
            MatchCriteria.Size, [Searched()], extensions: new ExtensionFilter(ExtensionFilterMode.SkipThese, [".jpg"])));

        Assert.Equal([20L, 30L, 40L], found.Groups.Select(group => group.Length!.Value).Order());
        Assert.Equal(["g.jpg.bak", "h.xjpg"], Names(found.Groups.Single(group => group.Length == 40)));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public async Task HiddenAndSystemFilesAreLeftOutUnlessAskedFor(FileAttributes attribute)
    {
        foreach (var file in new[] { _tree.File(10, "One", "a.bin"), _tree.File(10, "Two", "a.bin") })
        {
            File.SetAttributes(file, attribute);
        }

        var hidden = attribute == FileAttributes.Hidden;
        var byDefault = await _tree.FindAsync(MatchCriteria.Size, Searched());
        var asked = await _tree.FindAsync(new DuplicateSearch(
            MatchCriteria.Size, [Searched()], searchHidden: hidden, searchSystem: !hidden));

        Assert.Empty(byDefault.Groups);
        Assert.Equal(2, Assert.Single(asked.Groups).Files.Count);
    }

    /// <summary>A visible file inside a hidden folder is a visible file.</summary>
    [Fact]
    public async Task AFileInAHiddenFolderIsSearched()
    {
        _tree.File(10, "One", "a.bin");
        _tree.File(10, "Two", "a.bin");
        File.SetAttributes(Path.Combine(_tree.Top, "Two"), FileAttributes.Directory | FileAttributes.Hidden);

        var found = await _tree.FindAsync(MatchCriteria.Size, Searched());

        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
    }

    [Fact]
    public async Task EmptyFilesAreNeverMatched()
    {
        _tree.File(0, "One", "placeholder.txt");
        _tree.File(0, "Two", "placeholder.txt");

        var found = await _tree.FindAsync(MatchCriteria.Size | MatchCriteria.Name, Searched());

        Assert.Empty(found.Groups);
        Assert.Equal(2, found.LeftOut.Empty);
    }

    [Fact]
    public async Task AFileLinkIsNeverMatchedWithItsTarget()
    {
        var target = _tree.File(100, "One", "a.bin");
        Directory.CreateDirectory(Path.Combine(_tree.Top, "Two"));
        SymbolicLink.ToFile(Path.Combine(_tree.Top, "Two", "a.bin"), target);

        var found = await _tree.FindAsync(MatchCriteria.Size, Searched());

        Assert.Empty(found.Groups);
        Assert.Equal(1, found.LeftOut.Links);
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task ALinkInsideALocationIsNeverFollowed(DirectoryLinkKind kind)
    {
        _tree.File(100, "Searched", "a.bin");
        _tree.File(100, "Elsewhere", "a.bin");
        DirectoryLink.Create(kind, Path.Combine(_tree.Top, "Searched", "Link"), Path.Combine(_tree.Top, "Elsewhere"));

        var found = await _tree.FindAsync(MatchCriteria.Size, Searched("Searched"));

        Assert.Empty(found.Groups);
        Assert.Equal(1, found.LeftOut.Links);
    }

    [Fact]
    public async Task NamesAreComparedIgnoringCase()
    {
        _tree.File(100, "One", "Photo.JPG");
        _tree.File(100, "Two", "photo.jpg");
        _tree.File(100, "Two", "other.jpg");

        var found = await _tree.FindAsync(MatchCriteria.Name | MatchCriteria.Size, Searched());

        var names = Assert.Single(found.Groups).Files.Select(file => file.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("Photo.JPG", names);
        Assert.Contains("photo.jpg", names);
    }

    /// <summary>A name match alone is a match whatever the sizes, which is why §7.4 says such a group may differ.</summary>
    [Fact]
    public async Task ANameMatchAloneIgnoresTheLength()
    {
        _tree.File(10, "One", "report.pdf");
        _tree.File(20, "Two", "report.pdf");
        _tree.File(10, "Two", "other.pdf");

        var found = await _tree.FindAsync(MatchCriteria.Name, Searched());

        var group = Assert.Single(found.Groups);
        Assert.Null(group.Length);
        Assert.Equal(["report.pdf", "report.pdf"], Names(group));
    }

    /// <summary>
    /// Times compare to the file system's full precision (§7.4): one tick apart in the same minute is
    /// no match, whatever the files' lengths and names.
    /// </summary>
    [Fact]
    public async Task AModifiedTimeSearchComparesTheTimesToTheTick()
    {
        var instant = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc).AddTicks(5_358_979);
        File.SetLastWriteTimeUtc(_tree.File(10, "One", "a.jpg"), instant);
        File.SetLastWriteTimeUtc(_tree.File(20, "Two", "b.png"), instant);
        File.SetLastWriteTimeUtc(_tree.File(30, "Two", "c"), instant.AddTicks(1));

        var found = await _tree.FindAsync(MatchCriteria.Modified, Searched());

        var group = Assert.Single(found.Groups);
        Assert.Null(group.Length);
        Assert.Null(group.Name);
        Assert.Equal(instant, group.Modified);
        Assert.Equal(["a.jpg", "b.png"], Names(group));
    }

    /// <summary>
    /// The file table gives a file it could not size no length, which reads as none at all. Such a
    /// file may hold anything, so it is counted apart from an empty file, which the page says is never
    /// matched because its name is its content.
    /// </summary>
    [Fact]
    public void AFileWhoseLengthIsUnknownIsNotCountedAsEmpty()
    {
        var directory = MftRecord.ReservedRecordCount;
        var unsized = directory + 2;
        var fixture = new MftFixture()
            .AddDirectory(directory, MftRecord.RootRecordNumber, "Data")
            .AddFile(directory + 1, directory, "empty.bin", allocated: 0, logical: 0)
            .AddFileWithDataInAnExtensionRecord(
                unsized, directory, "unsized.bin", allocated: 4096, logical: 4096, extension: directory + 3, ListMismatch.ItsOwnSequence);

        var tree = MftExploreReader.Read(fixture.Build(), @"X:\", [], TableTuning.Default, onProgress: null, default).Tree!;
        var data = MftExploreReader.Locate(tree, ["Data"]).Node!.Value;

        // The fixture gives the file no length, and says so, which is the case under test.
        Assert.Equal(0, tree.LengthOf((int)unsized));
        Assert.True(tree.HasUnknownSizeBelow((int)unsized));

        var leftOut = Walk(tree, data, MatchCriteria.Size).LeftOut;

        Assert.Equal(1, leftOut.UnknownLength);
        Assert.Equal(1, leftOut.Empty);
    }

    /// <summary>A content match is a match on length, so a content search groups by length first.</summary>
    [Fact]
    public async Task AContentSearchGroupsByLength()
    {
        _tree.File(10, "One", "a.bin");
        _tree.File(10, "Two", "b.bin");
        _tree.File(20, "Two", "c.bin");

        var found = await _tree.FindAsync(MatchCriteria.Content, Searched());

        Assert.Equal(10L, Assert.Single(found.Groups).Length);
    }

    /// <summary>
    /// A file holding its own data behind a reparse point is a file like any other: deduplicated,
    /// compressed and cloud files all reach a group, and only the content leaves a cloud file out,
    /// because reading it would download it. The file table is where those states are told apart.
    /// </summary>
    [Fact]
    public void ADeduplicatedCompressedOrCloudFileIsAFileLikeAnyOther()
    {
        const uint DeduplicationTag = 0x8000_0013;
        var directory = MftRecord.ReservedRecordCount;
        var fixture = new MftFixture()
            .AddDirectory(directory, MftRecord.RootRecordNumber, "Data")
            .AddFile(directory + 1, directory, "plain.bin", allocated: 4096, logical: 4096)
            .AddCompressedFile(directory + 2, directory, "compressed.bin", logical: 4096, onDisk: 1024)
            .AddCloudFile(directory + 3, directory, "cloud.bin", logical: 4096)
            .AddFileWithAttributes(
                directory + 4, directory, "deduplicated.bin", 4096, FileAttributes.Archive | FileAttributes.ReparsePoint, DeduplicationTag)
            .AddFileLink(directory + 5, directory, "link.bin", logical: 4096);

        var tree = MftExploreReader.Read(fixture.Build(), @"X:\", [], TableTuning.Default, onProgress: null, default).Tree!;
        var data = MftExploreReader.Locate(tree, ["Data"]).Node!.Value;

        Assert.Equal(
            ["cloud.bin", "compressed.bin", "deduplicated.bin", "plain.bin"],
            Names(Assert.Single(Walk(tree, data, MatchCriteria.Size).Groups)));

        var content = Walk(tree, data, MatchCriteria.Content);
        Assert.Equal(["compressed.bin", "deduplicated.bin", "plain.bin"], Names(Assert.Single(content.Groups)));
        Assert.Equal(1, content.LeftOut.OnlyInTheCloud);
        Assert.Equal(1, content.LeftOut.Links);
    }

    [Fact]
    public void TheFileTableSaysWhichFilesAreHiddenOrSystemFiles()
    {
        var fixture = new MftFixture()
            .AddFileWithAttributes(MftRecord.ReservedRecordCount, MftRecord.RootRecordNumber, "hidden.bin", 10, FileAttributes.Hidden)
            .AddFileWithAttributes(MftRecord.ReservedRecordCount + 1, MftRecord.RootRecordNumber, "system.bin", 10, FileAttributes.System)
            .AddFileWithAttributes(MftRecord.ReservedRecordCount + 2, MftRecord.RootRecordNumber, "plain.bin", 10, FileAttributes.Archive);

        var tree = MftExploreReader.Read(fixture.Build(), @"X:\", [], TableTuning.Default, onProgress: null, default).Tree!;

        FileVisibility Of(string name) =>
            tree.VisibilityOf(tree.ChildrenOf(tree.RootNode).ToArray().Single(node => tree.NameOf(node) == name));

        Assert.Equal(FileVisibility.Hidden, Of("hidden.bin"));
        Assert.Equal(FileVisibility.System, Of("system.bin"));
        Assert.Equal(FileVisibility.Shown, Of("plain.bin"));
    }

    private static (IReadOnlyList<IReadOnlyList<FoundFile>> Groups, LeftOutFiles LeftOut) Walk(ExploreTree tree, int node, MatchCriteria criteria)
    {
        var volumes = new FakeVolumeInventory();
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", volumes), LocationRole.Search, DriveX);
        var walk = new CandidateWalk(new DuplicateSearch(criteria, [root.Given]));

        walk.Read(Scanned(tree, node), root, [], new PassedOverBelow(new UnresolvedReferences([]), below: null), IdentityRoute.FileId, default);

        return (CandidateGrouping.ByTheTree(walk.Found, criteria, CandidateGrouping.TreeMinuteOf), walk.LeftOut);
    }

    /// <summary><paramref name="node"/> of a tree read whole from a file table.</summary>
    private static ScannedFolder Scanned(ExploreTree tree, int node) =>
        new(tree, node, ScanStrategy.MasterFileTable, FallbackReason.None, FromIncompleteTable: false);
}
