using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Core.Tests.HiddenSpaceTests;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Explore page reads about the volume a scan is taken of: started with the scan, so the
/// slow parts finish while it runs, and finished with the tree and a free space read as it ends.
/// </summary>
public sealed class ScannedVolumeTests
{
    /// <summary>How long a reading started off the test's thread is given, far longer than it needs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheFiguresAreReadOnceForTheScannedVolumeInThePlainFormOfItsRoot()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var hidden = new FakeHiddenSpaceSource { Answer = Shadow(allocated: 1_000) };

        var volume = await ScannedVolume.Start(volumes, hidden, @"\\?\D:\", default).SpaceAsync(Tree(@"\\?\D:\", Svi.Absent));

        Assert.Equal([@"D:\"], hidden.Asked);
        Assert.Equal(new VolumeSpace(12_000, 6_000, Shadow(allocated: 1_000)), volume);
    }

    /// <summary>
    /// Windows is asked about the hidden space as the scan starts, so the shadow copy service is
    /// waited on while the scan runs rather than between its end and the map.
    /// </summary>
    [Fact]
    public void WindowsIsAskedAboutTheHiddenSpaceBeforeTheScanFinishes()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var hidden = new FakeHiddenSpaceSource();

        ScannedVolume.Start(volumes, hidden, @"D:\", default);

        Assert.True(SpinWait.SpinUntil(() => hidden.Asked.Count > 0, Patience), "Windows was never asked while the scan ran.");
        Assert.Equal([@"D:\"], hidden.Asked);
    }

    /// <summary>
    /// The page starts the reading on the window's thread, and where a planning pass has dropped the
    /// remembered list, reading it asks every volume the machine has mounted. Starting never waits
    /// for that reading.
    /// </summary>
    [Fact]
    public async Task StartingNeverWaitsForTheListOfVolumes()
    {
        using var gate = new ManualResetEventSlim();
        var volumes = new ListHeldUntil(gate, new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000));

        var starting = Task.Run(() => ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\", default));

        Assert.Same(starting, await Task.WhenAny(starting, Task.Delay(Patience)));

        gate.Set();

        Assert.Equal(new VolumeSpace(12_000, 6_000), await (await starting).SpaceAsync(Tree(@"D:\", Svi.Absent)));
    }

    [Fact]
    public async Task AScanOfAFolderAsksWindowsNothing()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var hidden = new FakeHiddenSpaceSource { Answer = Shadow(allocated: 1_000) };

        var volume = await ScannedVolume.Start(volumes, hidden, @"D:\work", default).SpaceAsync(Tree(@"D:\work", Svi.Absent));

        Assert.Empty(hidden.Asked);
        Assert.Equal(VolumeSpace.None, volume);
    }

    /// <summary>
    /// The free space is the volume's as the scan ends, read from that one volume, and not the
    /// figure the remembered list held as it started: everything else drawn describes the disk as
    /// the scan found it.
    /// </summary>
    [Fact]
    public async Task TheFreeSpaceIsReadAsTheScanEnds()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var reading = ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\", default);

        volumes.Without(@"D:\").With(@"D:\", totalBytes: 12_000, freeBytes: 5_000);

        Assert.Equal(new VolumeSpace(12_000, 5_000), await reading.SpaceAsync(Tree(@"D:\", Svi.Absent)));
    }

    /// <summary>
    /// The map is drawn without waiting for the list of every mounted volume, which asks an empty
    /// optical drive or a sleeping disk too.
    /// </summary>
    [Fact]
    public async Task TheSpaceDoesNotWaitForTheListOfVolumes()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var release = volumes.HoldRefreshes();

        var space = ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\", default).SpaceAsync(Tree(@"D:\", Svi.Absent));

        Assert.Same(space, await Task.WhenAny(space, Task.Delay(Patience)));
        Assert.Equal(1, volumes.RefreshCount);

        release();

        Assert.Equal(new VolumeSpace(12_000, 6_000), await space);
    }

    /// <summary>
    /// A letter <c>subst</c> made for the top of a volume is the whole of that volume, and its free
    /// space is read from the volume the letter leads to, which is mounted where the letter is not.
    /// </summary>
    [Fact]
    public async Task ALetterStandingForTheTopOfAVolumeIsGivenThatVolumesSpace()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"D:\", totalBytes: 12_000, freeBytes: 6_000)
            .Substituting(@"T:\", @"D:\");

        var volume = await ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"T:\", default).SpaceAsync(Tree(@"T:\", Svi.Absent));

        Assert.Equal(new VolumeSpace(12_000, 6_000), volume);
    }

    /// <summary>
    /// A walk is refused System Volume Information even as administrator, and marks it unknown with
    /// nothing counted. A folder the scan read any of, or every size of, has had its storage counted,
    /// and a part read is counted too, so what the scan read there is never drawn a second time.
    /// </summary>
    [Theory]
    [InlineData(Svi.Absent, false)]
    [InlineData(Svi.Refused, false)]
    [InlineData(Svi.Counted, true)]
    [InlineData(Svi.PartlyRead, true)]
    public async Task SystemVolumeInformationIsCountedWhereTheScanCountedAnythingInIt(Svi state, bool counted)
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);

        var volume = await ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\", default).SpaceAsync(Tree(@"D:\", state));

        Assert.Equal(counted, volume.CountedSystemVolumeInformation);
    }

    /// <summary>
    /// The volume is named from the list read again as the scan started, because a letter can have
    /// moved to another disk since the remembered list was read, and the name is what keeps a scan
    /// of one disk from being compared with the other's.
    /// </summary>
    [Fact]
    public async Task TheVolumeIsNamedOnceTheListHasBeenReadAgain()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", volumeName: @"\\?\Volume{d}\");
        var release = volumes.HoldRefreshes();

        var naming = ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\work", default).NameAsync();

        Assert.False(naming.IsCompleted);

        release();

        Assert.Equal(@"\\?\Volume{d}\", await naming);
    }

    /// <summary>
    /// The list is read under the scan's cancellation, and a cancel can reach that reading after the
    /// scan has finished, while the scan is being kept. A reading that was cancelled still names the
    /// volume, from the list the inventory kept. Cancelled here before it starts, which is the one
    /// moment a fake can cancel it for certain.
    /// </summary>
    [Fact]
    public async Task ACancelledReadingOfTheListStillNamesTheVolume()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", volumeName: @"\\?\Volume{d}\");

        var name = await ScannedVolume.Start(volumes, new FakeHiddenSpaceSource(), @"D:\work", new CancellationToken(canceled: true)).NameAsync();

        Assert.Equal(@"\\?\Volume{d}\", name);
    }

    /// <summary>An inventory whose remembered list is read only once <c>gate</c> is set, as a dropped list is read slowly.</summary>
    private sealed class ListHeldUntil(ManualResetEventSlim gate, IVolumeInventory machine) : IVolumeInventory
    {
        public IReadOnlyList<LocalVolume> Volumes
        {
            get
            {
                gate.Wait();
                return machine.Volumes;
            }
        }

        public string? MountPointOf(string path) => machine.MountPointOf(path);

        public string? VolumeNameOf(string mountPoint) => machine.VolumeNameOf(mountPoint);

        public IReadOnlyList<string> MountPointsOfVolume(string volumeName) => machine.MountPointsOfVolume(volumeName);

        public string? SubstituteOf(string driveRoot) => machine.SubstituteOf(driveRoot);

        public (long Total, long Free)? SpaceOf(string path) => machine.SpaceOf(path);

        public void Invalidate() => machine.Invalidate();

        public Task<IReadOnlyList<LocalVolume>> RefreshAsync(CancellationToken ct) => machine.RefreshAsync(ct);
    }
}
