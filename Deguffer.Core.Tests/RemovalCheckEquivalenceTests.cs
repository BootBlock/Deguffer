using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.2: the verdict of a policy that looks each rule up by folder is the verdict of asking every
/// rule in turn, over generated trees of nested and shared roots (#706).
///
/// <para>The oracle restates the rule the index replaced rather than sharing a line with it, so a
/// mistake in the index's walk cannot be matched by the same mistake here. It reads levels through
/// <see cref="LongPath.Relative"/> for the same reason.</para>
/// </summary>
public sealed class RemovalCheckEquivalenceTests
{
    private static readonly string[] Names = ["a", "b", "cache", "Outlook"];

    private const string Shared = "Shared";

    [Fact]
    public void AnIndexedPolicyAnswersAsAskingEveryRuleInTurnDoes()
    {
        var random = new Random(706);
        var volumes = new FakeVolumeInventory()
            .With(@"D:\", alsoMountedAt: [@"E:\Mount\"], volumeName: @"\\?\Volume{0e6b1d3a-1c2d-4e5f-8a9b-0c1d2e3f4a5b}\");
        var disk = new EveryPathIsAFolder();

        for (var scene = 0; scene < 40; scene++)
        {
            var regions = Regions(random);
            var declared = Roots(random, 30, "Declared");
            var probed = Roots(random, 15, "Probed");
            var policy = new ExploreActionPolicy(regions, declared, volumes, disk, probed);
            var oracle = new LinearPolicy(regions, declared, probed, volumes);

            for (var target = 0; target < 200; target++)
            {
                var path = RandomPath(random);
                var actual = policy.MayRemove(path);
                var expected = oracle.MayRemove(path);

                Assert.True(
                    (expected.IsAllowed, expected.Reason) == (actual.IsAllowed, actual.Reason),
                    $"'{path}' in scene {scene}: expected '{expected.Reason}', got '{actual.Reason}'.");

                Assert.Equal(oracle.RefusedAtAndBelow(path)?.Reason, policy.RefusedAtAndBelow(path)?.Reason);
            }
        }
    }

    /// <summary>
    /// A folder Windows gives back in the same device form it was asked about names no drive or
    /// folder a rule could be written in, so the shortcut for a path with no link on the way must not
    /// let it through.
    /// </summary>
    [Fact]
    public void AnItemNamedOnlyByItsVolumeIsRefusedThoughWindowsGivesTheSameNameBack()
    {
        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), new EveryPathIsAFolder());

        var verdict = policy.MayRemove(@"\\?\Volume{0e6b1d3a-1c2d-4e5f-8a9b-0c1d2e3f4a5b}\folder\big.bin");

        Assert.False(verdict.IsAllowed);
        Assert.Contains("no letter or folder", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule that names a folder only through a second mount of its volume is still found, and
    /// §5.2's refusal of an unrecognised sibling survives the index.
    /// </summary>
    [Fact]
    public void AnUnrecognisedSiblingIsRefusedAtEveryPlaceTheRootIsReachedAt()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"D:\", alsoMountedAt: [@"E:\Mount\"], volumeName: @"\\?\Volume{0e6b1d3a-1c2d-4e5f-8a9b-0c1d2e3f4a5b}\");
        ToolRoot root = ToolRoot.Folders(@"D:\Tool", "The tool's own folder.", name => name == "cache");
        var policy = new ExploreActionPolicy([], [root], volumes, new EveryPathIsAFolder());

        Assert.True(policy.MayRemove(@"D:\Tool\cache\x.bin").IsAllowed);
        Assert.False(policy.MayRemove(@"D:\Tool\settings\x.bin").IsAllowed);
        Assert.False(policy.MayRemove(@"E:\Mount\Tool\settings\x.bin").IsAllowed);
        Assert.False(policy.MayRemove(@"D:\Tool").IsAllowed);
    }

    /// <summary>
    /// A location no levels down is the target itself, which the passes above have answered, and a
    /// target is not refused for holding itself: here the permission ordered first has answered it.
    /// </summary>
    [Fact]
    public void AnAllowedFolderIsNotRefusedAsHoldingItself()
    {
        var policy = new ExploreActionPolicy(
            [
                ProtectedRegion.Permitting(@"D:\Shared\a", RegionScope.PathOnly),
                ProtectedRegion.Refusing(@"D:\Shared\a", RegionScope.PathOnly, "Refused by name only."),
            ],
            [],
            new FakeVolumeInventory(),
            new EveryPathIsAFolder());

        Assert.True(policy.MayRemove(@"D:\Shared\a").IsAllowed);
    }

    private static List<ProtectedRegion> Regions(Random random)
    {
        List<ProtectedRegion> regions = [];

        for (var region = 0; region < 6; region++)
        {
            var path = RandomFolder(random, Shared);
            var scope = random.Next(2) == 0 ? RegionScope.PathAndBelow : RegionScope.PathOnly;

            regions.Add(random.Next(2) == 0
                ? ProtectedRegion.Refusing(path, scope, $"Region {region} refuses.")
                : ProtectedRegion.Permitting(path, scope));
        }

        return regions;
    }

    private static List<ToolRoot> Roots(Random random, int count, string reason)
    {
        List<ToolRoot> roots = [];

        for (var root = 0; root < count; root++)
        {
            var path = RandomFolder(random, Shared);
            var recognised = Names[random.Next(Names.Length)];

            roots.Add(ToolRoot.Folders(path, $"{reason} {root} refuses.", name => name == recognised));
        }

        return roots;
    }

    private static string RandomFolder(Random random, string under) =>
        string.Join('\\', [@"D:\" + under, .. Enumerable.Range(0, random.Next(1, 5)).Select(_ => Names[random.Next(Names.Length)])]);

    private static string RandomPath(Random random)
    {
        var path = RandomFolder(random, Shared);

        return random.Next(4) == 0 ? path.Replace(@"D:\", @"E:\Mount\", StringComparison.Ordinal) : path;
    }

    /// <summary>
    /// <see cref="ExploreActionPolicy"/>'s passes as they were before they looked anything up: each
    /// rule asked of the path, the innermost kept, the first declared answering a tie.
    /// </summary>
    private sealed class LinearPolicy
    {
        private readonly List<(ProtectedRegion Region, ReachedFolder Folder)> _regions;
        private readonly List<(ToolRoot Root, ReachedFolder Folder)> _declared;
        private readonly List<(ToolRoot Root, ReachedFolder Folder)> _probed;
        private readonly List<(string Reason, ReachedFolder Folder)> _held;
        private readonly FakeVolumeInventory _volumes;

        public LinearPolicy(
            IEnumerable<ProtectedRegion> regions,
            IEnumerable<ToolRoot> declared,
            IEnumerable<ToolRoot> probed,
            FakeVolumeInventory volumes)
        {
            _volumes = volumes;
            _regions = [.. regions.OrderBy(r => r.Scope == RegionScope.PathOnly ? 0 : 1).Select(r => (r, ReachedFolder.At(r.Path, volumes)))];
            _declared = [.. declared.Select(r => (r, ReachedFolder.At(r.Path, volumes)))];
            _probed = [.. probed.Select(r => (r, ReachedFolder.At(r.Path, volumes)))];
            _held =
            [
                .. _regions.Where(r => !r.Region.Verdict.IsAllowed).Select(r => (r.Region.Verdict.Reason, r.Folder)),
                .. _declared.Concat(_probed).Select(r => (r.Root.Reason, r.Folder)),
            ];
        }

        public ExploreVerdict MayRemove(string path)
        {
            var target = LongPath.Entry(path)!;
            var places = VolumeRoot.Places(_volumes, target)!;
            var folder = ReachedFolder.Following(target, places);
            ExploreVerdict? allowed = null;

            foreach (var place in folder.Places)
            {
                var verdict = Above(place);

                if (!verdict.IsAllowed)
                {
                    return verdict;
                }

                allowed ??= verdict;
            }

            return Held(target, folder) ?? allowed!;
        }

        public ExploreVerdict? RefusedAtAndBelow(string path)
        {
            var target = LongPath.Entry(path)!;

            foreach (var place in ReachedFolder.Following(target, VolumeRoot.Places(_volumes, target)!).Places)
            {
                if (RefusedWithEverythingIn(place) is { } refused)
                {
                    return refused;
                }
            }

            return null;
        }

        private ExploreVerdict? Held(string target, ReachedFolder folder)
        {
            foreach (var ((reason, located), _) in _held
                         .Select(location => (Location: location, Levels: LevelsOf(folder.PathTo(location.Folder))))
                         .Where(location => location.Levels > 0)
                         .OrderBy(location => location.Levels))
            {
                var named = folder.Naming(located, target)!;

                return ExploreVerdict.Refuse(
                    $"'{Path.GetFileName(target)}' holds '{named}', and removing a folder removes "
                    + $"everything in it. Deguffer refuses '{Path.GetFileName(named)}' itself: {reason}");
            }

            return null;
        }

        /// <summary>The region table's rule for a whole folder, which the policy asks before anything else that is a region's.</summary>
        private ExploreVerdict? RefusedWithEverythingIn(string place)
        {
            ProtectedRegion? innermost = null;
            int? levels = null;

            foreach (var (region, folder) in _regions)
            {
                if (region.Scope != RegionScope.PathAndBelow || LevelsOf(folder, place) is not { } below || below >= levels)
                {
                    continue;
                }

                innermost = region;
                levels = below;
            }

            if (innermost is not { Verdict.IsAllowed: false } refusing)
            {
                return null;
            }

            var carvedOut = _regions.Any(r =>
                r.Region.Verdict.IsAllowed
                && r.Folder.Places.Any(inside =>
                    LongPath.Contains(place, inside) && !inside.Equals(place, StringComparison.OrdinalIgnoreCase)));

            return carvedOut ? null : refusing.Verdict;
        }

        private ExploreVerdict Above(string place)
        {
            if (RefusedWithEverythingIn(place) is { } everything)
            {
                return everything;
            }

            var region = InnermostRegion(place);

            if (region is { Verdict.IsAllowed: false })
            {
                return region.Verdict;
            }

            var verdict = Below(place);

            return verdict.IsAllowed && Probed(place) is { } probed ? probed : verdict;
        }

        private ProtectedRegion? InnermostRegion(string place)
        {
            ProtectedRegion? innermost = null;
            int? levels = null;

            foreach (var (region, folder) in _regions)
            {
                if (LevelsOf(folder, place) is not { } below
                    || (region.Scope == RegionScope.PathOnly && below > 0)
                    || below >= levels)
                {
                    continue;
                }

                innermost = region;
                levels = below;
            }

            return innermost;
        }

        private ExploreVerdict Below(string place)
        {
            List<(ToolRoot Root, ReachedFolder Folder)> innermost = [];
            int? levels = null;

            foreach (var root in _declared)
            {
                if (LevelsOf(root.Folder, place) is not { } below || below > levels)
                {
                    continue;
                }

                if (below != levels)
                {
                    innermost.Clear();
                    levels = below;
                }

                innermost.Add(root);
            }

            ExploreVerdict? refusal = null;

            foreach (var (root, folder) in innermost)
            {
                if (Refused(root, root.Path, folder.Naming(place, root.Path)!) is not { } refused)
                {
                    return ExploreVerdict.Unclassified;
                }

                refusal ??= refused;
            }

            return refusal ?? ExploreVerdict.Unclassified;
        }

        private ExploreVerdict? Probed(string place)
        {
            ExploreVerdict? refusal = null;
            int? levels = null;

            foreach (var (root, folder) in _probed)
            {
                if (LevelsOf(folder, place) is not { } below
                    || below >= levels
                    || folder.Naming(place, root.Path) is not { } named
                    || Refused(root, root.Path, named) is not { } refused)
                {
                    continue;
                }

                var name = Path.GetFileName(named);

                refusal = below == 0
                    ? refused
                    : ExploreVerdict.Refuse(
                        $"'{name}' is inside '{root.Path}', and Deguffer refuses what is in there "
                        + $"as well as '{Path.GetFileName(root.Path)}' itself: {root.Reason}");
                levels = below;
            }

            return refusal;
        }

        private static ExploreVerdict? Refused(ToolRoot root, string rootPath, string target)
        {
            if (target.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return ExploreVerdict.Refuse(root.Reason);
            }

            var child = target[rootPath.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)[0];

            return root.Recognises(new ToolRootChild(child, ChildKind.Folder))
                ? null
                : ExploreVerdict.Refuse(
                    $"'{child}' is not something Deguffer recognises inside '{rootPath}'. Configuration "
                    + "and credentials sit beside a cache in a tool's own folder, so anything unrecognised "
                    + "there is left alone.");
        }

        /// <summary>The nearest of the folder's places, chosen as the policy chose before it read levels from the text.</summary>
        private static int? LevelsOf(ReachedFolder folder, string place) =>
            folder.Places
                .Where(outer => LongPath.Contains(outer, place))
                .Select(outer => LevelsOf(LongPath.Relative(outer, place)))
                .Min();

        private static int? LevelsOf(string? relative) =>
            relative is null ? null : relative == "." ? 0 : relative.Split('\\').Length;
    }

    /// <summary>A disk where every path is a folder leading to itself, so every rule's location is present.</summary>
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
