using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What one <see cref="ExploreActionPolicy.MayRemove"/> costs on a machine that declares hundreds of
/// tool roots. A duplicate search asks it for every name of every confirmed copy, on the thread that
/// would otherwise be reading the disk, so its cost has to follow the depth of the path and not the
/// number of rules (#706).
/// </summary>
public sealed class RemovalCheckCostTests
{
    private const int DeclaredRoots = 400;
    private const int ProbedRoots = 100;
    private const int Calls = 2_000;

    /// <summary>A kibibyte, the budget the issue sets for one check.</summary>
    private const long Budget = 1024;

    private static readonly string Target = string.Join(
        Path.DirectorySeparatorChar,
        @"D:\Photos\2026\Trips\Coast\Day1\Morning\Camera\Originals\Raw\Set\Roll",
        "copy.bin");

    [Fact]
    public void OneCheckAllocatesLessThanAKibibyteHoweverManyRootsAreDeclared()
    {
        var policy = PolicyOf(DeclaredRoots, ProbedRoots);

        Assert.True(policy.MayRemove(Target).IsAllowed);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var call = 0; call < Calls; call++)
        {
            policy.MayRemove(Target);
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;

        Assert.True(perCall < Budget, $"One check allocated {perCall} bytes, over the budget of {Budget}.");
    }

    /// <summary>Allocation alone, which no other work on the machine can move.</summary>
    [Fact]
    public void ACheckAllocatesTheSameForAThousandRootsAsForTen()
    {
        var few = PolicyOf(declared: 10, probed: 0);
        var many = PolicyOf(declared: 1_000, probed: 0);

        few.MayRemove(Target);
        many.MayRemove(Target);

        Assert.Equal(AllocatedByOneCall(few), AllocatedByOneCall(many));
    }

    /// <summary>
    /// A scan of the rules allocates nothing once each question does not, so only the time can show
    /// one. In processor time, compared as <see cref="ThreadCpu.Growth"/> does, with a limit far from
    /// both the flat answer and the scan's.
    /// </summary>
    [Fact]
    public void ACheckTakesNoLongerForThousandsOfRootsThanForTen()
    {
        const int ChecksInABlock = 100;

        var few = PolicyOf(declared: 10, probed: 0);
        var many = PolicyOf(declared: 4_000, probed: 0);

        void Check(ExploreActionPolicy policy)
        {
            for (var call = 0; call < ChecksInABlock; call++)
            {
                policy.MayRemove(Target);
            }
        }

        var growth = ThreadCpu.Growth(() => Check(few), () => Check(many), times: 1, TimeSpan.FromMilliseconds(250));

        Assert.True(growth < 3, $"A check took {growth:F1} times as long for 4,000 roots as for 10.");
    }

    private static long AllocatedByOneCall(ExploreActionPolicy policy)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var call = 0; call < Calls; call++)
        {
            policy.MayRemove(Target);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
    }

    private static ExploreActionPolicy PolicyOf(int declared, int probed) =>
        new(
            [
                ProtectedRegion.Refusing(@"D:\Windows", RegionScope.PathAndBelow, "Windows."),
                ProtectedRegion.Permitting(@"D:\Photos", RegionScope.PathAndBelow),
            ],
            Roots("Tools", declared),
            new StillVolumes(),
            new EveryPathIsAFolder(),
            Roots("Probed", probed));

    private static IReadOnlyList<ToolRoot> Roots(string under, int count) =>
    [
        .. Enumerable.Range(0, count)
            .Select(index => ToolRoot.Folders($@"D:\{under}\Tool{index}", "A tool's own folder.", name => name == "cache")),
    ];

    /// <summary>One volume, answered without the recording <see cref="FakeVolumeInventory"/> does, which would be measured with the policy.</summary>
    private sealed class StillVolumes : IVolumeInventory
    {
        public IReadOnlyList<LocalVolume> Volumes => [];

        public string? MountPointOf(string path) => @"D:\";

        public string? VolumeNameOf(string mountPoint) => null;

        public IReadOnlyList<string> MountPointsOfVolume(string volumeName) => [];

        public string? SubstituteOf(string driveRoot) => null;

        public (long Total, long Free)? SpaceOf(string path) => null;

        public void Invalidate()
        {
        }

        public Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct) => Task.FromResult(Volumes);
    }

    /// <summary>A disk where every path is a folder that leads to itself, which is what a path with no link on the way answers.</summary>
    private sealed class EveryPathIsAFolder : IFileSystem
    {
        private static readonly IFileSystem Inner = EmptyFileSystem.Instance;

        public PathPresence ProbeDirectory(string path) => Inner.ProbeDirectory(path);

        public PathPresence ProbeEntry(string path) => Inner.ProbeEntry(path);

        public PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink) =>
            Inner.ProbeEntry(path, out isDirectory, out isLink);

        public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
            Inner.ListEntries(directory, request, into);

        public long? TryGetFileLength(string path) => Inner.TryGetFileLength(path);

        public long? TryGetNewestFileTime(string path) => Inner.TryGetNewestFileTime(path);

        public void DeleteFile(string path) => Inner.DeleteFile(path);

        public RefusalReason? ProbeRemoval(string path) => Inner.ProbeRemoval(path);

        public void DeleteDirectory(string path) => Inner.DeleteDirectory(path);

        public void ClearAttributes(string path) => Inner.ClearAttributes(path);

        public FileAttributes? TryGetAttributes(string path) => FileAttributes.Directory;

        public bool MayExist(string path) => true;

        public string? FinalPath(string path) => path;
    }
}
