using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The pass that searches the folders the user approved, and the one location it refuses to search.
///
/// A cloud client that mounts its storage as a drive letter is a fixed, ready volume no per-entry test
/// can tell from a disk, so enumerating one downloads the user's files instead of measuring them. The
/// Explore picker and the Recycle Bin provider both refuse such a volume already. This pass reaches one
/// by a third route — an approved source folder — and that route is the user's own deliberate choice,
/// so it is refused by default and searched where they have said to search it.
/// </summary>
public sealed class SourceDirectoryDiscoveryTests : IDisposable
{
    /// <summary>The flag word measured on a Google Drive mount. See <see cref="LocalVolume.StoresContentRemotely"/>.</summary>
    private const VolumeFeatures CloudMount = (VolumeFeatures)0x0000_0106;

    private const VolumeFeatures LocalDisk = (VolumeFeatures)0x03E7_2EFF;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The defect this exists for. The folder is approved, it holds a directory the pass is looking
    /// for, and the pass never opens it — because opening it is the download.
    /// </summary>
    [Fact]
    public async Task DoesNotSearchAnApprovedFolderOnACloudMount()
    {
        var root = Project();

        var found = await Discovery(CloudMount).FindAsync([new SourceRoot(root)]);

        Assert.Empty(found.Candidates);
        Assert.Equal([root], found.RefusedRoots);
    }

    /// <summary>
    /// The other half of the decision. Refusing a folder the user pointed Deguffer at by hand, with no
    /// way to say otherwise, would be worse than not refusing it — so the approval they gave in
    /// Settings is honoured, and the whole point of storing it is that it is honoured here.
    /// </summary>
    [Fact]
    public async Task SearchesACloudMountWhereTheUserHasApprovedIt()
    {
        var root = Project();

        var found = await Discovery(CloudMount).FindAsync(
            [new SourceRoot(root, RemoteStorageApproved: true)]);

        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.Empty(found.RefusedRoots);
    }

    [Fact]
    public async Task SearchesAnApprovedFolderOnAnOrdinaryDisk()
    {
        var root = Project();

        var found = await Discovery(LocalDisk).FindAsync([new SourceRoot(root)]);

        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.Empty(found.RefusedRoots);
    }

    /// <summary>
    /// A volume the inventory says nothing about is searched, as every volume was before Deguffer read
    /// volume flags at all. Refusing on no reading would take the tool away from a share and from any
    /// volume that declined the query.
    /// </summary>
    [Fact]
    public async Task SearchesAFolderOnAVolumeItKnowsNothingAbout()
    {
        var root = Project();

        var found = await Discovery(volumes: new FakeVolumeInventory()).FindAsync([new SourceRoot(root)]);

        Assert.Equal([Path.Combine(root, "Example", "obj")], found.Candidates);
        Assert.Empty(found.RefusedRoots);
    }

    /// <summary>
    /// One refused folder does not cost the others. The user's remaining source folders are searched
    /// exactly as they were, which is what keeps the refusal proportionate to what it is about.
    ///
    /// <para>The refused folder is on a volume no test creates anything on, which also pins the order
    /// of the two checks: the refusal comes before the existence check, so a root is reported as
    /// refused rather than quietly skipped as not attached. A gate that sat behind another check is one
    /// an edit to that check can switch off.</para>
    /// </summary>
    [Fact]
    public async Task RefusesOnlyTheFolderOnTheCloudMount()
    {
        var local = Project("local");

        var volumes = new FakeVolumeInventory()
            .With(Path.GetPathRoot(local)!, features: LocalDisk)
            .With(@"V:\", features: CloudMount);

        var found = await Discovery(volumes: volumes).FindAsync(
            [new SourceRoot(@"V:\work"), new SourceRoot(local)]);

        Assert.Equal([Path.Combine(local, "Example", "obj")], found.Candidates);
        Assert.Equal([@"V:\work"], found.RefusedRoots);
    }

    /// <summary>
    /// The judgement on its own, for the caller that reached a root some other way. Explore's
    /// declaration of in-use build directories applies it rather than restating it, because two routes
    /// over the same roots that disagree about which they may read are two different rules.
    /// </summary>
    [Fact]
    public void SaysWhetherItWouldSearchARoot()
    {
        var root = Project();
        var discovery = Discovery(CloudMount);

        Assert.False(discovery.Searches(new SourceRoot(root)));
        Assert.True(discovery.Searches(new SourceRoot(root, RemoteStorageApproved: true)));
    }

    /// <summary>
    /// The remembered answer is keyed on the approval as well as the folder. Every provider sharing one
    /// discovery asks with the same roots in the same pass, and a memo that ignored the flag would hand
    /// one of them an empty result for a folder it is allowed to search — or the reverse, which is the
    /// download.
    /// </summary>
    [Fact]
    public async Task DoesNotAnswerAnApprovedRootWithTheResultForAnUnapprovedOne()
    {
        var root = Project();
        var discovery = Discovery(CloudMount);

        var refused = await discovery.FindAsync([new SourceRoot(root)]);
        var searched = await discovery.FindAsync([new SourceRoot(root, RemoteStorageApproved: true)]);

        Assert.Empty(refused.Candidates);
        Assert.Equal([Path.Combine(root, "Example", "obj")], searched.Candidates);
    }

    /// <summary>
    /// The volume list is a snapshot, and a cloud client that mounted itself while Deguffer was open
    /// would be missing from a stale one — which is the direction that ends in a download. A planning
    /// pass drops it along with everything else it remembers about the machine.
    /// </summary>
    [Fact]
    public void DropsTheVolumeListWhenAPassInvalidatesIt()
    {
        var volumes = new FakeVolumeInventory();

        Discovery(volumes: volumes).Invalidate();

        Assert.Equal(1, volumes.InvalidateCount);
    }

    /// <summary>A folder holding one project with an <c>obj</c> in it, which is what the pass looks for.</summary>
    private string Project(string name = "src")
    {
        var root = _temp.CreateDirectory(name);

        Directory.CreateDirectory(Path.Combine(root, "Example", "obj"));

        return root;
    }

    /// <summary>
    /// A pass over a machine whose volumes this test states. The scratch tree's own volume is named,
    /// rather than the real inventory consulted, so nothing here depends on what the developer's temp
    /// directory happens to be mounted on.
    ///
    /// <para>The scanner has no volume index, which is the contract that sends the pass down the walk —
    /// so what these tests refuse or allow is the enumeration itself.</para>
    /// </summary>
    private SourceDirectoryDiscovery Discovery(
        VolumeFeatures features = LocalDisk, IVolumeInventory? volumes = null)
    {
        var discovery = new SourceDirectoryDiscovery(
            new FakeDirectoryScanner(),
            volumes ?? new FakeVolumeInventory().With(Path.GetPathRoot(_temp.Path)!, features: features));

        discovery.Include(["obj"]);

        return discovery;
    }
}
