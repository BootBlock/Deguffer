using Deguffer.Core.Scanning.Mft;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.5's fast path, measured against a tree whose sizes are known by construction.
///
/// The failure mode this guards is not a crash. An MFT reader that misparses reports a plausible
/// number, and a plausible wrong number in a disk cleaner is how a user is told a 4 GB cache is
/// empty — or that an empty one is worth clearing.
/// </summary>
public class MftVolumeIndexTests
{
    // A synthetic profile tree. Paths are invented rather than copied from a real machine.
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;
    private const uint Nested = 9;
    private const uint Sibling = 10;

    private static MftFixture Tree() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache")
        .AddDirectory(Nested, Cache, "content-v2")
        .AddDirectory(Sibling, Profile, ".config");

    private static MftVolumeIndex Build(MftFixture fixture)
    {
        using var source = fixture.Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, out var index));
        return index;
    }

    /// <summary>
    /// A partial index is worse than none. It still answers every query, and answers some of them
    /// short — a 4 GB cache reported as 200 MB, with nothing to distinguish that from the truth.
    /// Refusing costs a slow scan; accepting costs a wrong number that decides a deletion.
    /// </summary>
    [Fact]
    public void RefusesToBuildAnIndexFromATableItCouldNotFullyRead()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFile(21, Cache, "unreachable.tgz", allocated: 4_000_000_000, logical: 4_000_000_000)
            .UnreadableFrom(21)
            .Build();

        Assert.False(MftVolumeIndexBuilder.TryBuild(source, out _));
    }

    [Fact]
    public void TotalsAFileTreeItHasNotWalked()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4000)
            .AddFile(21, Cache, "b.tgz", allocated: 8192, logical: 8100)
            .AddFile(22, Nested, "deep.tgz", allocated: 2048, logical: 1500));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"]);

        Assert.NotNull(size);
        Assert.Equal(4096 + 8192 + 2048, size!.Value.Allocated);
        Assert.Equal(4000 + 8100 + 1500, size.Value.Logical);
        Assert.False(size.Value.IsApproximate);
    }

    /// <summary>
    /// §5.6 in the scanner: asserting the target totalled correctly is half a test. A subtree walk
    /// that escapes upward through the parent links would still produce the right number for the
    /// target and quietly include everything beside it.
    /// </summary>
    [Fact]
    public void ExcludesSiblingsOfTheMeasuredDirectory()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "counted.tgz", allocated: 4096, logical: 4096)
            .AddFile(21, Sibling, "untouched.json", allocated: 999_999, logical: 999_999));

        var cache = index.TryMeasure(["Users", "testuser", ".npm-cache"]);
        var sibling = index.TryMeasure(["Users", "testuser", ".config"]);

        Assert.Equal(4096, cache!.Value.Allocated);
        Assert.Equal(999_999, sibling!.Value.Allocated);
    }

    /// <summary>
    /// The compressed and sparse case — the reason <see cref="Deguffer.Core.Scanning.ScanSize"/>
    /// carries two numbers. A walk over <c>FileInfo.Length</c> sees only the logical one and would
    /// promise 10 GB back from a tree that yields 2.
    /// </summary>
    [Fact]
    public void ReportsAllocatedAndLogicalSeparatelyForCompressedFiles()
    {
        var index = Build(Tree().AddFile(20, Cache, "compressed.bin", allocated: 2_000_000, logical: 10_000_000));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value;

        Assert.Equal(2_000_000, size.Allocated);
        Assert.Equal(10_000_000, size.Logical);
        Assert.Equal(2_000_000, size.Reclaimable);
    }

    /// <summary>
    /// A file small enough to live in its own MFT record occupies no clusters, so deleting it frees
    /// none. Reporting its length as reclaimable would overstate what a cleanup can return.
    /// </summary>
    [Fact]
    public void CountsResidentFilesAsOccupyingNoClusters()
    {
        var index = Build(Tree().AddResidentFile(20, Cache, "tiny.json", length: 300));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value;

        Assert.Equal(0, size.Allocated);
        Assert.Equal(300, size.Logical);
    }

    /// <summary>
    /// The file is real and its size is not in the base record, so the table cannot say how big
    /// this subtree is. Returning a total anyway would report a cache short by whatever that file
    /// holds — the same failure <see cref="MftVolumeIndexBuilder"/> refuses to commit when it
    /// cannot read part of the table, arrived at one record later.
    /// </summary>
    [Fact]
    public void RefusesToTotalASubtreeHoldingAFileWhoseDataMovedToAnExtensionRecord()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFileWithDataInAnExtensionRecord(21, Cache, "fragmented.tgz"));

        Assert.Null(index.TryMeasure(["Users", "testuser", ".npm-cache"]));
    }

    /// <summary>
    /// Only the first extent of a split <c>$DATA</c> carries the sizes. A base record holding a
    /// later one has zeroes in those fields, and reading them as a size turns a large file into an
    /// empty one.
    /// </summary>
    [Fact]
    public void RefusesToTotalASubtreeHoldingAFileDescribingOnlyALaterExtent()
    {
        var index = Build(Tree().AddFileDescribingOnlyALaterExtent(20, Cache, "split.tgz"));

        Assert.Null(index.TryMeasure(["Users", "testuser", ".npm-cache"]));
    }

    [Fact]
    public void RefusesToTotalASubtreeHoldingAFileWithATruncatedDataHeader()
    {
        var index = Build(Tree().AddFileWithATruncatedDataHeader(20, Cache, "corrupt.tgz"));

        Assert.Null(index.TryMeasure(["Users", "testuser", ".npm-cache"]));
    }

    /// <summary>
    /// The refusal has to be local, or one unreadable record anywhere on the volume would send
    /// every path to the walk and the fast path would exist in name only.
    /// </summary>
    [Fact]
    public void StillTotalsSubtreesThatDoNotHoldTheUnestablishedFile()
    {
        var index = Build(Tree()
            .AddFileWithDataInAnExtensionRecord(20, Cache, "fragmented.tgz")
            .AddFile(21, Sibling, "settings.json", allocated: 1024, logical: 1000));

        Assert.Null(index.TryMeasure(["Users", "testuser", ".npm-cache"]));
        Assert.Equal(1024, index.TryMeasure(["Users", "testuser", ".config"])!.Value.Allocated);
    }

    /// <summary>
    /// The other direction, and the one that decides whether the fast path is usable at all: a file
    /// that genuinely occupies nothing must total as zero. A symbolic link carries no unnamed
    /// <c>$DATA</c>, and treating "none" as "unknown" would refuse a total for every tree holding
    /// one.
    /// </summary>
    [Fact]
    public void CountsAFileWithNoDataStreamAsOccupyingNothing()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFileWithNoDataStream(21, Cache, "link"));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// A directory's own <c>$DATA</c> is never counted, so a directory keeping its attributes
    /// elsewhere costs nothing to skip. Refusing there would take out every large directory on the
    /// volume, which is exactly where NTFS puts an attribute list.
    /// </summary>
    [Fact]
    public void StillTotalsADirectoryWhoseOwnAttributesMovedToAnExtensionRecord()
    {
        var index = Build(Tree()
            .AddDirectoryWithAttributesInAnExtensionRecord(30, Cache, "many-entries")
            .AddFile(20, 30, "a.tgz", allocated: 4096, logical: 4096));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// A base record whose names moved into extension records is skipped, not refused. NTFS does
    /// this once a file has enough hard links to overflow its own record, and a system volume is
    /// full of them — refusing would take the fast path off the volume that matters most, every
    /// time, for a shape that is not a fault.
    /// </summary>
    [Fact]
    public void BuildsAnIndexFromATableHoldingRecordsWhoseNamesLiveElsewhere()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddRecordWithNamesInExtensionRecords(21));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// The same record without an attribute list is a different thing: in use, holding data,
    /// claiming no identity and pointing nowhere else for one. Nothing can place it, so the index
    /// is refused rather than built around it.
    /// </summary>
    [Fact]
    public void RefusesToBuildAnIndexFromARecordThatClaimsNoIdentityAtAll()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddRecordWithNoIdentityAtAll(21)
            .Build();

        Assert.False(MftVolumeIndexBuilder.TryBuild(source, out _));
    }

    /// <summary>
    /// A link inside a measured tree contributes nothing, because the walk does not enter one
    /// either. Its declared size belongs to whatever it points at, which keeps its own place in
    /// the table and is counted there if it is counted at all.
    /// </summary>
    [Fact]
    public void CountsNothingForALinkInsideAMeasuredTree()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFileLink(21, Cache, "elsewhere.tgz", logical: 9_999_999));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value;

        Assert.Equal(4096, size.Allocated);
        Assert.Equal(4096, size.Logical);
    }

    /// <summary>
    /// Not every reparse point is a link. A file compressed in place by CompactOS carries one, and
    /// its bytes are genuinely on the volume — the filter driver hides the attribute from an
    /// ordinary enumeration, so the walk counts the file. An index that read "reparse point" as
    /// "link" would report those bytes as nothing, which is the under-reporting direction.
    /// </summary>
    [Fact]
    public void CountsAFileThatCarriesAReparsePointButIsNotALink()
    {
        var index = Build(Tree()
            .AddOverlayCompressedFile(20, Cache, "packed.tgz", allocated: 2048, logical: 8192));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value;

        Assert.Equal(2048, size.Allocated);
        Assert.Equal(8192, size.Logical);
    }

    /// <summary>
    /// The truncation the enumerator lets through: an attribute is admitted at 0x10 bytes, and the
    /// resident branch reads a length field at 0x10. Reading it unguarded throws out of the index
    /// build, where <see cref="MftVolumeIndexCache"/> catches only <see cref="IOException"/> and
    /// the failure escapes into the scan rather than turning into a slow one.
    /// </summary>
    [Fact]
    public void RefusesToTotalASubtreeHoldingAFileWithATruncatedResidentDataHeader()
    {
        var index = Build(Tree().AddFileWithATruncatedResidentDataHeader(20, Cache, "corrupt.json"));

        Assert.Null(index.TryMeasure(["Users", "testuser", ".npm-cache"]));
    }

    /// <summary>
    /// A fragmented file still knows its own size: the extent at VCN 0 carries it and the
    /// continuation extents do not. Letting a later extent overwrite what the first one established
    /// would drop a whole tree to the walk for a file that was never in doubt.
    /// </summary>
    [Fact]
    public void TotalsAFileSplitAcrossExtentsFromTheExtentThatCarriesTheSizes()
    {
        var index = Build(Tree()
            .AddFileSplitAcrossExtents(20, Cache, "big.tgz", allocated: 40_960, logical: 40_000));

        var size = index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value;

        Assert.Equal(40_960, size.Allocated);
        Assert.Equal(40_000, size.Logical);
    }

    /// <summary>
    /// A junction's target keeps its own place in the table, so the link itself has no children
    /// however much its path appears to hold. Totalling it would report a populated cache as empty,
    /// which is the one answer §5.5 will not tolerate — the walk follows the link and is right.
    /// </summary>
    [Fact]
    public void RefusesToMeasureAPathReachedThroughALink()
    {
        var index = Build(Tree()
            .AddDirectoryLink(30, Profile, "linked-cache")
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096));

        Assert.Null(index.TryMeasure(["Users", "testuser", "linked-cache"]));

        // And the refusal is confined to the path that runs through the link.
        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// The same refusal for a path that merely passes through a link on its way down. Nothing in a
    /// healthy table hangs entries below a junction — whatever it stands for keeps its own place —
    /// so this holds the rule at every level rather than trusting that shape never arrives.
    /// </summary>
    [Fact]
    public void RefusesToMeasureAPathWithALinkPartWayDownIt()
    {
        var index = Build(Tree()
            .AddDirectoryLink(30, Profile, "linked")
            .AddDirectory(31, 30, "inner")
            .AddFile(32, 31, "a.tgz", allocated: 4096, logical: 4096));

        Assert.Null(index.TryMeasure(["Users", "testuser", "linked", "inner"]));
    }

    [Fact]
    public void FindsDirectoriesRegardlessOfPathCasing()
    {
        var index = Build(Tree().AddFile(20, Cache, "a.tgz", allocated: 1024, logical: 1024));

        Assert.Equal(1024, index.TryMeasure(["USERS", "TestUser", ".NPM-Cache"])!.Value.Allocated);
    }

    /// <summary>
    /// Returning zero for an unknown path would render as "this cache is already clear". Null is
    /// the signal to fall back to the walk instead, so the distinction has to survive.
    /// </summary>
    [Fact]
    public void ReturnsNullForAPathTheTableDoesNotContain()
    {
        var index = Build(Tree());

        Assert.Null(index.TryMeasure(["Users", "testuser", ".does-not-exist"]));
        Assert.Null(index.TryMeasure(["Users", "nobody", ".npm-cache"]));
    }

    [Fact]
    public void MeasuresTheWholeVolumeFromTheRoot()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFile(21, Sibling, "b.json", allocated: 1024, logical: 1024));

        Assert.Equal(5120, index.TryMeasure([])!.Value.Allocated);
    }

    /// <summary>
    /// The update sequence fixup, exercised where it actually matters: a record whose size field
    /// lies across a sector boundary, so NTFS has displaced two of its bytes into the array. A
    /// reader that does not restore them reports a size wrong by up to 2^48.
    /// </summary>
    [Fact]
    public void RestoresSizeFieldBytesDisplacedByTheSectorStamp()
    {
        const long Allocated = 0x0000_1234_5678_9ABC;

        var index = Build(Tree().AddFileWithSizeAcrossSectorBoundary(20, Cache, Allocated, logical: Allocated));

        Assert.Equal(Allocated, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// A torn write must take the record out entirely: half-applying the fixup leaves two wrong
    /// bytes per sector, which lands inside a size field often enough to matter. Taking the record
    /// out means the table no longer describes a file that exists, so the index goes with it.
    ///
    /// This used to keep the rest of the table and answer anyway. That answer was 4096 for a
    /// directory holding 12288 bytes, with nothing to distinguish it from the truth — the same
    /// failure <see cref="MftVolumeIndexBuilder"/> already refuses to commit for a region it could
    /// not read. The cost of refusing is a slow scan, which §5.5 makes visible and §6.3 makes the
    /// ordinary case anyway.
    /// </summary>
    [Fact]
    public void RefusesToBuildAnIndexWhenARecordWasTornMidWrite()
    {
        using var source = Tree()
            .AddFile(20, Cache, "good.tgz", allocated: 4096, logical: 4096)
            .AddFile(21, Cache, "torn.tgz", allocated: 8192, logical: 8192)
            .CorruptSectorStamp(21)
            .Build();

        Assert.False(MftVolumeIndexBuilder.TryBuild(source, out _));
    }

    /// <summary>
    /// The refusal above must not fire on the records a healthy volume is full of. An extension
    /// record holds attributes belonging to another record's file, and skipping it is correct
    /// rather than a loss — the base record carries the identity and the size.
    /// </summary>
    [Fact]
    public void BuildsAnIndexFromATableHoldingExtensionRecords()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddExtensionRecord(21, baseRecordNumber: 20));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    [Fact]
    public void SkipsUnusedRecordsWithoutAttachingThemToTheTree()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddUnused(21)
            .AddUnused(22));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
        Assert.Equal(4096, index.TryMeasure([])!.Value.Allocated);
    }

    /// <summary>
    /// A directory record can carry its own <c>$DATA</c>. Counting it would double every file the
    /// directory contains, since those are counted through their own records.
    /// </summary>
    [Fact]
    public void IgnoresADirectorysOwnDataStream()
    {
        // Two directories are in the measured subtree (.npm-cache and content-v2), each carrying a
        // non-zero $DATA stream in the fixture. Counting either would show up here.
        var index = Build(Tree().AddFile(20, Nested, "a.tgz", allocated: 4096, logical: 4096));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }

    /// <summary>
    /// A parent reference this reader cannot address is a name it could not read, which makes the
    /// record one that is in use and cannot be placed — so the index is refused rather than built
    /// without it. Distinct from a record naming a parent the table simply does not hold, which is
    /// an ordinary live-volume race and is dropped.
    ///
    /// The narrowing this guards is still the point. The stray record names parent 0x1_0000_0007;
    /// truncated to 32 bits that is record 7, the profile directory — so a reader that narrows
    /// silently attaches it there, builds an index quite happily, and fails this test.
    /// </summary>
    [Fact]
    public void RefusesToBuildAnIndexFromARecordNamingAParentItCannotAddress()
    {
        using var source = Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFileWithUnaddressableParent(21, "stray.tgz", allocated: 1_000_000)
            .Build();

        Assert.False(MftVolumeIndexBuilder.TryBuild(source, out _));
    }

    [Fact]
    public void SurvivesARecordCountLargerThanTheTreeItDescribes()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddUnused(400));

        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
    }
}
