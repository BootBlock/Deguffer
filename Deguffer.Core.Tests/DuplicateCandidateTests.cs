using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which files a search keeps, what role each has, and how they are grouped before anything is read
/// (§7.4).
/// </summary>
public sealed class DuplicateCandidateTests : IDisposable
{
    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private SearchLocation Searched(params string[] segments) => new(Path.Combine([_tree.Top, .. segments]));

    private static IEnumerable<string> Names(CandidateGroup group) => group.Files.Select(file => file.Name).Order();

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
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", volumes), LocationRole.Search);
        var reference = new ResolvedLocation(
            new(@"X:\Data\Photos", LocationRole.Reference), @"X:\Data\Photos", ReachedFolder.At(@"X:\Data\Photos", volumes), LocationRole.Reference);

        var walk = new CandidateWalk(new DuplicateSearch(MatchCriteria.Size, [root.Given]));
        walk.Read(tree, tree.RootNode, root, [reference], below: null, default);

        var group = Assert.Single(CandidateGrouping.Group(walk.Found, MatchCriteria.Size));
        Assert.Equal(LocationRole.Reference, group.Files.Single(file => file.Name == "a.jpg").Role);
        Assert.Equal(LocationRole.Search, group.Files.Single(file => file.Name == "b.jpg").Role);
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

        var found = await _tree.FindAsync(new DuplicateSearch(
            MatchCriteria.Size, [Searched()], extensions: new ExtensionFilter(ExtensionFilterMode.SkipThese, [".jpg"])));

        Assert.Equal([20L, 30L], found.Groups.Select(group => group.Length!.Value).Order());
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

    private static (IReadOnlyList<CandidateGroup> Groups, LeftOutFiles LeftOut) Walk(ExploreTree tree, int node, MatchCriteria criteria)
    {
        var volumes = new FakeVolumeInventory();
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", volumes), LocationRole.Search);
        var walk = new CandidateWalk(new DuplicateSearch(criteria, [root.Given]));

        walk.Read(tree, node, root, [], below: null, default);

        return (CandidateGrouping.Group(walk.Found, criteria), walk.LeftOut);
    }
}
