using System.Runtime.InteropServices;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// One file is one file however many paths reach it and however many names it has, and the files of
/// a search are known by their identities, never their paths (§7.4).
/// </summary>
public sealed class DuplicateIdentityTests : IDisposable
{
    private const int AccessDenied = 5;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private SearchLocation Searched(params string[] segments) => new(Path.Combine([_tree.Top, .. segments]));

    private string At(params string[] segments) => Path.Combine([_tree.Top, .. segments]);

    private static bool NoIdentity(SafeFileHandle handle, IdentityRoute route, out FileIdentity identity)
    {
        identity = default;
        return false;
    }

    /// <summary>Opens each path as Windows does, except the file <paramref name="refused"/>, which it will not describe.</summary>
    private static FileInformation Refusing(string refused, int error) => new(
        (path, use) =>
        {
            if (use is HandleUse.Describe && LongPath.Display(path).Equals(refused, StringComparison.Ordinal))
            {
                Marshal.SetLastPInvokeError(error);
                return new SafeFileHandle(-1, ownsHandle: false);
            }

            return FileInformation.Open(path, use);
        },
        FileInformation.ReadIdentity);

    /// <summary>
    /// The walk lists each name of a linked file, so without identities the two names of one file
    /// would be a group of their own, and the file would be offered for removal against itself.
    /// </summary>
    [Fact]
    public async Task AHardLinkedFileIsOneFileWithEveryNameOnTheWalk()
    {
        var first = _tree.File(100, "Data", "a.bin");
        var second = HardLink.To(first, At("Data", "Other", "b.bin"));
        var copy = _tree.File(100, "Data", "c.bin");

        var found = await _tree.FindAsync(MatchCriteria.Size, Searched("Data"));

        var group = Assert.Single(found.Groups);
        Assert.Equal(2, group.Files.Count);
        var linked = Assert.Single(group.Files, file => file.HasSeveralNames);
        Assert.Equal(2, linked.NameCount);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), linked.Names.Order(StringComparer.Ordinal));
        Assert.Equal([copy], Assert.Single(group.Files, file => !file.HasSeveralNames).Names);
    }

    /// <summary>
    /// The file table keeps one name a record, so the tree never holds the second name, and only
    /// asking Windows for the file's names finds it.
    /// </summary>
    [Fact]
    public void AHardLinkedFileIsOneFileWithEveryNameOnTheFileTable()
    {
        var first = _tree.File(100, "Data", "a.bin");
        var second = HardLink.To(first, At("Data", "b.bin"));
        var copy = _tree.File(100, "Data", "c.bin");
        var directory = MftRecord.ReservedRecordCount;
        var fixture = new MftFixture()
            .AddDirectory(directory, MftRecord.RootRecordNumber, "Data")
            .AddFile(directory + 1, directory, "a.bin", allocated: 4096, logical: 100)
            .AddFile(directory + 2, directory, "c.bin", allocated: 4096, logical: 100);

        var groups = Identify(Walk(fixture, _tree.Top), MatchCriteria.Size);

        var files = Assert.Single(groups).Files;
        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), Assert.Single(files, file => file.HasSeveralNames).Names.Order(StringComparer.Ordinal));
        Assert.Equal(copy, Assert.Single(files, file => !file.HasSeveralNames).Path);
    }

    /// <summary>
    /// The locations of a search are resolved before anything is read, and this is the second line:
    /// two trees reaching one folder, here through a junction, still give each file once, at the
    /// path Windows says it is at.
    /// </summary>
    [Fact]
    public void TwoPathsToOneFolderGiveEachFileOnce()
    {
        _tree.File(100, "Real", "Data", "x.bin");
        _tree.File(100, "Real", "Data", "y.bin");
        Junction.ToDirectory(At("Alias"), At("Real"));
        var fixture = new MftFixture()
            .AddDirectory(MftRecord.ReservedRecordCount, MftRecord.RootRecordNumber, "Data")
            .AddFile(MftRecord.ReservedRecordCount + 1, MftRecord.ReservedRecordCount, "x.bin", allocated: 4096, logical: 100)
            .AddFile(MftRecord.ReservedRecordCount + 2, MftRecord.ReservedRecordCount, "y.bin", allocated: 4096, logical: 100);

        var groups = Identify([.. Walk(fixture, At("Real")), .. Walk(fixture, At("Alias"))], MatchCriteria.Size);

        Assert.Equal(
            [At("Real", "Data", "x.bin"), At("Real", "Data", "y.bin")],
            Assert.Single(groups).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A file reached once in a reference location is a reference however else it was reached, so it
    /// is never offered for removal by the path that was searched.
    /// </summary>
    [Fact]
    public void AFileReachedAsAReferenceIsAReference()
    {
        _tree.File(100, "Real", "Data", "x.bin");
        _tree.File(100, "Real", "Data", "y.bin");
        Junction.ToDirectory(At("Alias"), At("Real"));
        var fixture = new MftFixture()
            .AddDirectory(MftRecord.ReservedRecordCount, MftRecord.RootRecordNumber, "Data")
            .AddFile(MftRecord.ReservedRecordCount + 1, MftRecord.ReservedRecordCount, "x.bin", allocated: 4096, logical: 100)
            .AddFile(MftRecord.ReservedRecordCount + 2, MftRecord.ReservedRecordCount, "y.bin", allocated: 4096, logical: 100);

        var groups = Identify(
            [.. Walk(fixture, At("Real")), .. Walk(fixture, At("Alias"), LocationRole.Reference)],
            MatchCriteria.Size);

        Assert.All(Assert.Single(groups).Files, file => Assert.Equal(LocationRole.Reference, file.Role));
    }

    /// <summary>
    /// A file Windows would not describe may still be there, so it is counted apart from one that is
    /// gone: reading a refusal as absence is how a copy taken for gone becomes the last one removed.
    /// </summary>
    [Theory]
    [InlineData(AccessDenied, 0, 1)]
    [InlineData(FileAttributeRead.FileNotFound, 1, 0)]
    public async Task AFileWindowsWillNotDescribeIsLeftOutAndNeverCountedAsGone(int error, int gone, int unidentified)
    {
        _tree.File(100, "Data", "a.bin");
        _tree.File(100, "Data", "b.bin");
        var refused = _tree.File(100, "Data", "c.bin");

        var found = await _tree.Finder(files: Refusing(refused, error)).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [Searched("Data")]), _tree.Policy());

        Assert.Equal(["a.bin", "b.bin"], Assert.Single(found.Groups).Files.Select(file => file.Name).Order());
        Assert.Equal(gone, found.LeftOut.Gone);
        Assert.Equal(unidentified, found.LeftOut.Unidentified);
    }

    /// <summary>
    /// The tree dates a file to the minute, so a file it holds no date for is identified first and
    /// placed by the time Windows gives; it still matches a file the tree did date, to the tick.
    /// </summary>
    [Fact]
    public void AFileTheTableHoldsNoTimeForIsStillMatchedToTheTick()
    {
        var instant = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc).AddTicks(5_358_979);
        File.SetLastWriteTimeUtc(_tree.File(10, "Data", "a.bin"), instant);
        File.SetLastWriteTimeUtc(_tree.File(20, "Data", "b.bin"), instant);
        File.SetLastWriteTimeUtc(_tree.File(30, "Data", "c.bin"), instant.AddTicks(1));
        var directory = MftRecord.ReservedRecordCount;
        var fixture = new MftFixture()
            .AddDirectory(directory, MftRecord.RootRecordNumber, "Data")
            .AddFileWithNoTimestamps(directory + 1, directory, "a.bin", logical: 10)
            .AddFile(directory + 2, directory, "b.bin", allocated: 4096, logical: 20, lastWritten: instant)
            .AddFile(directory + 3, directory, "c.bin", allocated: 4096, logical: 30, lastWritten: instant.AddTicks(1));

        var group = Assert.Single(Identify(Walk(fixture, _tree.Top), MatchCriteria.Modified));

        Assert.Equal(instant, group.Modified);
        Assert.Equal(["a.bin", "b.bin"], group.Files.Select(file => file.Name).Order());
    }

    /// <summary>
    /// Without an identity a file reached by two paths cannot be told from two copies, so a volume
    /// where none can be had is not searched, and every location on it is named, a reference too.
    /// </summary>
    [Fact]
    public async Task AVolumeWhereNoFileCanBeIdentifiedIsNamedAndNotSearched()
    {
        _tree.File(100, "Data", "a.bin");
        _tree.File(100, "Data", "b.bin");
        _tree.File(100, "Data", "Photos", "c.bin");
        var photos = new SearchLocation(At("Data", "Photos"), LocationRole.Reference);

        var found = await _tree.Finder(files: new FileInformation(FileInformation.Open, NoIdentity)).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [Searched("Data"), photos]), _tree.Policy());

        Assert.Empty(found.Groups);
        Assert.Empty(found.Read);
        Assert.Equal([Searched("Data"), photos], found.Unsearched.Select(location => location.Given));
        Assert.All(found.Unsearched, location => Assert.Contains("which file is which", location.Reason, StringComparison.Ordinal));
    }

    /// <summary>
    /// A volume that answers only the older call is identified by it for every file, never by a mix
    /// of the two, whose serial numbers differ in width and would make one file two.
    /// </summary>
    [Fact]
    public async Task AVolumeThatAnswersOnlyTheOlderCallIsIdentifiedByIt()
    {
        var first = _tree.File(100, "Data", "a.bin");
        HardLink.To(first, At("Data", "b.bin"));
        _tree.File(100, "Data", "c.bin");
        var olderOnly = new FileInformation(
            FileInformation.Open,
            (SafeFileHandle handle, IdentityRoute route, out FileIdentity identity) =>
                route is IdentityRoute.Legacy
                    ? FileInformation.ReadIdentity(handle, route, out identity)
                    : NoIdentity(handle, route, out identity));

        var found = await _tree.Finder(files: olderOnly).FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [Searched("Data")]), _tree.Policy());

        var files = Assert.Single(found.Groups).Files;
        Assert.Equal(2, files.Count);
        Assert.Single(files, file => file.HasSeveralNames);
        Assert.All(files, file => Assert.True(file.Identity.Volume <= uint.MaxValue));
    }

    private IReadOnlyList<FoundFile> Walk(MftFixture fixture, string top, LocationRole role = LocationRole.Search)
    {
        var tree = MftExploreReader.Read(
            fixture.Build(), top + Path.DirectorySeparatorChar, [], TableTuning.Default, onProgress: null, default).Tree!;
        var node = MftExploreReader.Locate(tree, ["Data"]).Node!.Value;
        var folder = Path.Combine(top, "Data");
        var volume = new LocalVolume(_tree.Top + Path.DirectorySeparatorChar, DriveType.Fixed, VolumeReadiness.Ready);
        var root = new ResolvedLocation(new(folder, role), folder, ReachedFolder.At(folder, _tree.Volumes), role, volume);
        var walk = new CandidateWalk(new DuplicateSearch(MatchCriteria.Size, [root.Given]), new UnresolvedReferences([]));

        walk.Read(
            new ScannedFolder(tree, node, ScanStrategy.MasterFileTable, FallbackReason.None, FromIncompleteTable: false),
            root, [], below: null, IdentityRoute.FileId, default);

        return walk.Found;
    }

    private static IReadOnlyList<CandidateGroup> Identify(IReadOnlyList<FoundFile> found, MatchCriteria criteria) =>
        new CandidateIdentification(FileInformation.Default, criteria).Group(found, default);
}
