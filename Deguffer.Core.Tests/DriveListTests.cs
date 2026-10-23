using System.Collections.Specialized;
using System.ComponentModel;
using Deguffer.Core.Exploring;
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

        var drives = new DriveList(volumes, new ManualTimeProvider());

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

        var drives = new DriveList(volumes, new ManualTimeProvider());

        await Refresh(drives);

        Assert.Equal([@"C:\"], drives.Entries.Select(entry => entry.RootPath));
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

        var drives = new DriveList(volumes, new ManualTimeProvider());

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
        var drives = new DriveList(volumes, time);

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
    /// opens, and that read waits on every device the machine has.
    /// </summary>
    [Fact]
    public async Task NeverDropsTheListToReadItWhereItStands()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var drives = new DriveList(volumes, new ManualTimeProvider());

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
        var drives = new DriveList(volumes, new ManualTimeProvider());
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
        var drives = new DriveList(volumes, new ManualTimeProvider());

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
        var drives = new DriveList(volumes, time);

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
        Assert.Equal([nameof(DriveEntry.Choice)], rowChanges);
    }

    [Fact]
    public async Task AReadingThatChangedNothingTellsNobodyAnything()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 40 * Gigabyte);
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time);

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
        var drives = new DriveList(volumes, time);

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
        var drives = new DriveList(new FakeVolumeInventory().With(@"D:\"), new ManualTimeProvider());

        await Refresh(drives);

        Assert.Same(drives.Entries[0], drives.Find(@"d:\"));
        Assert.Null(drives.Find(@"E:\"));
        Assert.Null(drives.Find(null));
    }

    /// <summary>The drive the reader chose stays chosen for as long as it is offered.</summary>
    [Fact]
    public async Task AChosenDriveThatIsStillOfferedStaysChosen()
    {
        var drives = new DriveList(new FakeVolumeInventory().With(@"C:\").With(@"D:\"), new ManualTimeProvider());

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
            new ManualTimeProvider());

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
            new ManualTimeProvider());

        await Refresh(drives);

        Assert.Equal(@"A:\", drives.Choose(null)?.RootPath);
        Assert.Null(new DriveList(new FakeVolumeInventory(), new ManualTimeProvider()).Choose(null));
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
            new ManualTimeProvider());

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
            new ManualTimeProvider());

        await Refresh(drives);

        Assert.Equal(@"C:\Mount\", drives.Holding(@"S:\2024")?.RootPath);
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
