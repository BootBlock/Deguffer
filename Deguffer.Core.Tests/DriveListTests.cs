using System.Collections.Specialized;
using System.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Health;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Explore drive picker's list: which volumes it offers, in what order, how often it asks the
/// machine, and what the picker is told when it does.
///
/// <para>The last of those is the one a crash turned on. Each reading replaced the selected entry
/// with a newer value, and a <c>ComboBox</c> opening its list in that moment asked for item -1 and
/// took the application down. So the tests below count the changes the list reports, not only what
/// it ends up holding.</para>
/// </summary>
public sealed class DriveListTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public async Task OffersTheDrivesInDriveLetterOrder()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"E:\")
            .With(@"C:\")
            .With(@"D:\")
            .With(@"C:\Mount\");

        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal([@"C:\", @"C:\Mount\", @"D:\", @"E:\"], drives.Entries.Select(entry => entry.RootPath));
    }

    [Fact]
    public async Task LeavesOutWhatCannotBeScanned()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\")
            .With(@"D:\", DriveType.CDRom, readiness: VolumeReadiness.NoMedia)
            .With(@"N:\", DriveType.Network);

        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.LeftOut, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal([@"C:\"], drives.Entries.Select(entry => entry.RootPath));
    }

    /// <summary>
    /// Explore pictures a share by walking it, so a mapped letter is offered beside the local drives,
    /// in letter order, and not refused. It is never probed, so it is never taken for an empty drive.
    /// </summary>
    [Fact]
    public async Task OffersAMappedNetworkDriveWhereAskedTo()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"Z:\", DriveType.Network, readiness: VolumeReadiness.NotAsked)
            .With(@"C:\")
            .With(@"D:\", DriveType.CDRom, readiness: VolumeReadiness.NoMedia);

        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal([@"C:\", @"Z:\"], drives.Entries.Select(entry => entry.RootPath));

        var mapped = drives.Entries[1].Choice;
        Assert.True(mapped.IsNetwork);
        Assert.False(mapped.IsRefused);
        Assert.False(drives.Entries[0].Choice.IsNetwork);
    }

    /// <summary>
    /// A mapped drive is offered to a person who chooses it, and not opened on by default while a
    /// local drive will scan: the page would picture another machine at the speed of the link.
    /// </summary>
    [Fact]
    public async Task TheDefaultIsALocalDriveBeforeAMappedOne()
    {
        var drives = new DriveList(
            new FakeVolumeInventory()
                .With(@"A:\", DriveType.Network, readiness: VolumeReadiness.NotAsked)
                .With(@"B:\", features: VolumeFeatures.RemoteStorage)
                .With(@"C:\"),
            new ManualTimeProvider(),
            NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"C:\", drives.Choose(null)?.RootPath);
        Assert.Equal(@"A:\", drives.Choose(@"a:\")?.RootPath);
    }

    /// <summary>Where only a mapped drive will scan, it is the default rather than a refused local one.</summary>
    [Fact]
    public async Task WhereOnlyAMappedDriveWillScanItIsTheDefault()
    {
        var drives = new DriveList(
            new FakeVolumeInventory()
                .With(@"B:\", features: VolumeFeatures.RemoteStorage)
                .With(@"Z:\", DriveType.Network, readiness: VolumeReadiness.NotAsked),
            new ManualTimeProvider(),
            NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"Z:\", drives.Choose(null)?.RootPath);
    }

    /// <summary>
    /// A volume Windows would not describe is not an empty drive. The user can see it in File
    /// Explorer, so it is listed, refused, and told why, as a cloud mount is.
    /// </summary>
    [Fact]
    public async Task ListsAVolumeWindowsWillNotDescribeAndRefusesIt()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\")
            .With(@"R:\", readiness: VolumeReadiness.Refused);

        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        var refused = Assert.Single(drives.Entries, entry => entry.RootPath == @"R:\").Choice;
        Assert.Equal(DriveChoice.UnreadableRefusal, refused.Refusal);
        Assert.False(Assert.Single(drives.Entries, entry => entry.RootPath == @"C:\").Choice.IsRefused);
    }

    /// <summary>
    /// Opening the picker twice in a minute reads the machine once. Every reading moved the system
    /// drive's free space, so a reading per open was a changed entry per open.
    /// </summary>
    [Fact]
    public async Task ReadsTheMachineOnlyOnceTheLastReadingIsAMinuteOld()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time, NetworkDrives.Offered, new HealthByVolume());

        Assert.True(await Refresh(drives));
        Assert.Equal(1, volumes.RefreshCount);

        time.Advance(DriveList.FreshFor - TimeSpan.FromSeconds(1));

        Assert.False(await Refresh(drives));
        Assert.Equal(1, volumes.RefreshCount);

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await Refresh(drives));
        Assert.Equal(2, volumes.RefreshCount);
    }

    /// <summary>
    /// The picker reads the machine through the inventory's own off-thread reading, never by
    /// dropping the list and reading it where it stands: a picker refreshes on the UI thread as it
    /// opens, and that read waits on every volume the machine has mounted.
    /// </summary>
    [Fact]
    public async Task NeverDropsTheListToReadItWhereItStands()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        Assert.True(await Refresh(drives));

        Assert.Equal(1, volumes.RefreshCount);
        Assert.Equal(0, volumes.InvalidateCount);
    }

    /// <summary>
    /// The picker opened while the page's own reading is still under way does not start a second
    /// one, which would be the same devices asked twice for one look at the list.
    /// </summary>
    [Fact]
    public async Task AReadingAskedForWhileOneRunsIsTheSameReading()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());
        var release = volumes.HoldRefreshes();

        var first = drives.ReadAsync(CancellationToken.None);
        var second = drives.ReadAsync(CancellationToken.None);

        // Asked before the release, so a second reading that started would be held and fail here
        // rather than leave the test waiting on it.
        Assert.False(first.IsCompleted, "The first reading was meant to be under way.");
        Assert.True(second.IsCompleted, "A second reading was started while the first ran.");
        Assert.Null(await second);
        Assert.Equal(1, volumes.RefreshCount);

        release();

        Assert.NotNull(await first);
    }

    /// <summary>A reading that was stopped read nothing, so it does not stand for a minute.</summary>
    [Fact]
    public async Task AStoppedReadingLeavesTheNextOneToRead()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var drives = new DriveList(volumes, new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drives.ReadAsync(new CancellationToken(canceled: true)));

        Assert.True(await Refresh(drives));
        Assert.Equal([@"C:\"], drives.Entries.Select(entry => entry.RootPath));
    }

    /// <summary>
    /// The crash. A drive whose figures moved keeps its row, the row carries the new figures, and the
    /// list itself is told nothing, so the picker's selection is never taken away from it.
    /// </summary>
    [Fact]
    public async Task ADriveWhoseFiguresMovedIsWrittenOverWhereItSits()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 40 * Gigabyte)
            .With(@"D:\", totalBytes: 100 * Gigabyte, freeBytes: 70 * Gigabyte);

        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time, NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        var system = drives.Entries[0];
        var listChanges = WatchList(drives);
        var rowChanges = WatchRow(system);

        volumes.Without(@"C:\").With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 39 * Gigabyte);
        time.Advance(DriveList.FreshFor);

        await Refresh(drives);

        Assert.Empty(listChanges);
        Assert.Same(system, drives.Entries[0]);
        Assert.Equal(39 * Gigabyte, system.Choice.FreeBytes);
        Assert.Equal([nameof(DriveEntry.Choice), nameof(DriveEntry.Description)], rowChanges);
    }

    [Fact]
    public async Task AReadingThatChangedNothingTellsNobodyAnything()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 40 * Gigabyte);
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time, NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        var listChanges = WatchList(drives);
        var rowChanges = WatchRow(drives.Entries[0]);

        time.Advance(DriveList.FreshFor);
        await Refresh(drives);

        Assert.Empty(listChanges);
        Assert.Empty(rowChanges);
    }

    /// <summary>
    /// A drive plugged in goes in at its own letter, and one taken away costs one removal. Neither
    /// disturbs the rows around it.
    /// </summary>
    [Fact]
    public async Task ADriveThatComesOrGoesCostsOneChange()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"F:\");
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time, NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        var system = drives.Entries[0];
        var listChanges = WatchList(drives);

        volumes.With(@"D:\");
        time.Advance(DriveList.FreshFor);
        await Refresh(drives);

        Assert.Equal([NotifyCollectionChangedAction.Add], listChanges);
        Assert.Equal([@"C:\", @"D:\", @"F:\"], drives.Entries.Select(entry => entry.RootPath));

        listChanges.Clear();

        volumes.Without(@"F:\");
        time.Advance(DriveList.FreshFor);
        await Refresh(drives);

        Assert.Equal([NotifyCollectionChangedAction.Remove], listChanges);
        Assert.Same(system, drives.Entries[0]);
    }

    [Fact]
    public async Task FindsARowByItsMountPointWhateverTheCase()
    {
        var drives = new DriveList(new FakeVolumeInventory().With(@"D:\"), new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Same(drives.Entries[0], drives.Find(@"d:\"));
        Assert.Null(drives.Find(@"E:\"));
        Assert.Null(drives.Find(null));
    }

    /// <summary>The drive the reader chose stays chosen for as long as it is offered.</summary>
    [Fact]
    public async Task AChosenDriveThatIsStillOfferedStaysChosen()
    {
        var drives = new DriveList(new FakeVolumeInventory().With(@"C:\").With(@"D:\"), new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Same(drives.Find(@"D:\"), drives.Choose(@"d:\"));
    }

    /// <summary>
    /// A refused volume is listed and not defaulted onto: opening the page on a drive whose Scan
    /// button is dead reads as an app that failed to start. Taking the first row would put a cloud
    /// mount lettered before the system drive in the box.
    /// </summary>
    [Fact]
    public async Task TheDefaultIsTheFirstDriveExploreWillScan()
    {
        var drives = new DriveList(
            new FakeVolumeInventory()
                .With(@"A:\", features: VolumeFeatures.RemoteStorage)
                .With(@"C:\"),
            new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"C:\", drives.Choose(null)?.RootPath);
        Assert.Equal(@"C:\", drives.Choose(@"E:\")?.RootPath);
    }

    /// <summary>Where every volume is refused there is nothing better to name, and the page says why it will not scan it.</summary>
    [Fact]
    public async Task WhereEveryDriveIsRefusedTheFirstIsNamed()
    {
        var drives = new DriveList(
            new FakeVolumeInventory()
                .With(@"A:\", features: VolumeFeatures.RemoteStorage)
                .With(@"B:\", features: VolumeFeatures.RemoteStorage),
            new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"A:\", drives.Choose(null)?.RootPath);
        Assert.Null(new DriveList(new FakeVolumeInventory(), new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume()).Choose(null));
    }

    /// <summary>
    /// The volume holding a folder is asked of the inventory, so a folder under a volume mounted at
    /// a folder names that volume rather than the disk the mount point sits on.
    /// </summary>
    [Fact]
    public async Task TheDriveHoldingAFolderIsTheVolumeItIsOn()
    {
        var drives = new DriveList(
            new FakeVolumeInventory().With(@"C:\").With(@"C:\Mount\").With(@"D:\"),
            new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"C:\Mount\", drives.Holding(@"C:\Mount\Photos")?.RootPath);
        Assert.Equal(@"C:\", drives.Holding(@"C:\Users\testuser")?.RootPath);
        Assert.Null(drives.Holding(@"\\server.test\share\folder"));
    }

    /// <summary>
    /// A folder reached through a letter <c>subst</c> made is on the volume the letter leads to, as a
    /// folder below a mount point is on the mounted volume.
    /// </summary>
    [Fact]
    public async Task TheDriveHoldingAFolderThroughALetterIsTheVolumeTheLetterLeadsTo()
    {
        var drives = new DriveList(
            new FakeVolumeInventory().With(@"C:\").With(@"C:\Mount\").Substituting(@"S:\", @"C:\Mount\Photos"),
            new ManualTimeProvider(), NetworkDrives.Offered, new HealthByVolume());

        await Refresh(drives);

        Assert.Equal(@"C:\Mount\", drives.Holding(@"S:\2024")?.RootPath);
    }

    [Fact]
    public async Task ARowNamesADriveHealthFoundAConcernWith()
    {
        var health = new HealthByVolume();
        var drive = new DriveDescription(new DriveKey("a"), 0, "System SSD");
        health.Record(new DriveVolumes(drive.Key, "C:", [@"C:\"]));
        health.Record(new DriveHealth(drive.Key, drive.Name, DriveVerdict.Failing, "summary", null, [], [], [], false));

        var drives = new DriveList(new FakeVolumeInventory().With(@"C:\").With(@"D:\"), new ManualTimeProvider(), NetworkDrives.Offered, health);
        await Refresh(drives);

        var system = drives.Entries[0];

        Assert.True(system.HasHealthConcern);
        Assert.Equal("Failing", system.Health?.Word);
        Assert.Contains("Drive health: Failing. System SSD reports a serious problem.", system.Description, StringComparison.Ordinal);
        Assert.False(drives.Entries[1].HasHealthConcern);
        Assert.Equal(drives.Entries[1].Choice.Description, drives.Entries[1].Description);
    }

    [Fact]
    public async Task ARowFollowsHealthsLatestReadingInPlace()
    {
        var health = new HealthByVolume();
        var drive = new DriveDescription(new DriveKey("a"), 0, "System SSD");
        var drives = new DriveList(new FakeVolumeInventory().With(@"C:\"), new ManualTimeProvider(), NetworkDrives.Offered, health);
        await Refresh(drives);
        var row = drives.Entries[0];
        var changes = WatchRow(row);

        health.Record(new DriveVolumes(drive.Key, "C:", [@"C:\"]));
        health.Record(new DriveHealth(drive.Key, drive.Name, DriveVerdict.Watch, "summary", null, [], [], [], false));

        Assert.Same(row, drives.Entries[0]);
        Assert.True(row.HasHealthConcern);
        Assert.Contains(nameof(DriveEntry.HasHealthConcern), changes);
        Assert.Contains(nameof(DriveEntry.Description), changes);

        health.Record(new DriveHealth(drive.Key, drive.Name, DriveVerdict.Good, "summary", null, [], [], [], false));

        Assert.False(row.HasHealthConcern);
        Assert.Null(row.Health);
    }

    /// <summary>A reading and its rows, as a page that has nothing to guard between the two takes them.</summary>
    private static async Task<bool> Refresh(DriveList drives)
    {
        if (await drives.ReadAsync(CancellationToken.None) is not { } read)
        {
            return false;
        }

        drives.Show(read);

        return true;
    }

    private static List<NotifyCollectionChangedAction> WatchList(DriveList drives)
    {
        var changes = new List<NotifyCollectionChangedAction>();

        drives.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);

        return changes;
    }

    private static List<string?> WatchRow(INotifyPropertyChanged row)
    {
        var changes = new List<string?>();

        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        return changes;
    }
}
