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
    /// A torn write must take the record out entirely. Half-applying the fixup leaves two wrong
    /// bytes per sector, which lands inside a size field often enough to matter.
    /// </summary>
    [Fact]
    public void RejectsARecordWhoseSectorStampWasTornAndKeepsTheRest()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "good.tgz", allocated: 4096, logical: 4096)
            .AddFile(21, Cache, "torn.tgz", allocated: 8192, logical: 8192)
            .CorruptSectorStamp(21));

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

    [Fact]
    public void DiscardsARecordNamingAParentItCannotAddress()
    {
        var index = Build(Tree()
            .AddFile(20, Cache, "a.tgz", allocated: 4096, logical: 4096)
            .AddFileWithUnaddressableParent(21, "stray.tgz", allocated: 1_000_000));

        // The stray record names parent 0x1_0000_0007. Truncated to 32 bits that is record 7 —
        // the profile directory — so a narrowing bug shows up as a megabyte appearing there.
        Assert.Equal(4096, index.TryMeasure(["Users", "testuser", ".npm-cache"])!.Value.Allocated);
        Assert.Equal(4096, index.TryMeasure(["Users", "testuser"])!.Value.Allocated);
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
