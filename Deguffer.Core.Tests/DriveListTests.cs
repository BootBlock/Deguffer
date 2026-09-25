using System.Collections.Specialized;
using System.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Tests.Fakes;

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
    public void OffersTheDrivesInDriveLetterOrder()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"E:\")
            .With(@"C:\")
            .With(@"D:\")
            .With(@"C:\Mount\");

        var drives = new DriveList(volumes, new ManualTimeProvider());

        drives.Refresh();

        Assert.Equal([@"C:\", @"C:\Mount\", @"D:\", @"E:\"], drives.Entries.Select(entry => entry.RootPath));
    }

    [Fact]
    public void LeavesOutWhatCannotBeScanned()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\")
            .With(@"D:\", DriveType.CDRom, isReady: false)
            .With(@"N:\", DriveType.Network);

        var drives = new DriveList(volumes, new ManualTimeProvider());

        drives.Refresh();

        Assert.Equal([@"C:\"], drives.Entries.Select(entry => entry.RootPath));
    }

    /// <summary>
    /// Opening the picker twice in a minute reads the machine once. Every reading moved the system
    /// drive's free space, so a reading per open was a changed entry per open.
    /// </summary>
    [Fact]
    public void ReadsTheMachineOnlyOnceTheLastReadingIsAMinuteOld()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\");
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time);

        Assert.True(drives.Refresh());
        Assert.Equal(1, volumes.InvalidateCount);

        time.Advance(DriveList.FreshFor - TimeSpan.FromSeconds(1));

        Assert.False(drives.Refresh());
        Assert.Equal(1, volumes.InvalidateCount);

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(drives.Refresh());
        Assert.Equal(2, volumes.InvalidateCount);
    }

    /// <summary>
    /// The crash. A drive whose figures moved keeps its row, the row carries the new figures, and the
    /// list itself is told nothing, so the picker's selection is never taken away from it.
    /// </summary>
    [Fact]
    public void ADriveWhoseFiguresMovedIsWrittenOverWhereItSits()
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 40 * Gigabyte)
            .With(@"D:\", totalBytes: 100 * Gigabyte, freeBytes: 70 * Gigabyte);

        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time);

        drives.Refresh();

        var system = drives.Entries[0];
        var listChanges = WatchList(drives);
        var rowChanges = WatchRow(system);

        volumes.Without(@"C:\").With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 39 * Gigabyte);
        time.Advance(DriveList.FreshFor);

        drives.Refresh();

        Assert.Empty(listChanges);
        Assert.Same(system, drives.Entries[0]);
        Assert.Equal(39 * Gigabyte, system.Choice.FreeBytes);
        Assert.Equal([nameof(DriveEntry.Choice)], rowChanges);
    }

    [Fact]
    public void AReadingThatChangedNothingTellsNobodyAnything()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\", totalBytes: 100 * Gigabyte, freeBytes: 40 * Gigabyte);
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time);

        drives.Refresh();

        var listChanges = WatchList(drives);
        var rowChanges = WatchRow(drives.Entries[0]);

        time.Advance(DriveList.FreshFor);
        drives.Refresh();

        Assert.Empty(listChanges);
        Assert.Empty(rowChanges);
    }

    /// <summary>
    /// A drive plugged in goes in at its own letter, and one taken away costs one removal. Neither
    /// disturbs the rows around it.
    /// </summary>
    [Fact]
    public void ADriveThatComesOrGoesCostsOneChange()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"F:\");
        var time = new ManualTimeProvider();
        var drives = new DriveList(volumes, time);

        drives.Refresh();

        var system = drives.Entries[0];
        var listChanges = WatchList(drives);

        volumes.With(@"D:\");
        time.Advance(DriveList.FreshFor);
        drives.Refresh();

        Assert.Equal([NotifyCollectionChangedAction.Add], listChanges);
        Assert.Equal([@"C:\", @"D:\", @"F:\"], drives.Entries.Select(entry => entry.RootPath));

        listChanges.Clear();

        volumes.Without(@"F:\");
        time.Advance(DriveList.FreshFor);
        drives.Refresh();

        Assert.Equal([NotifyCollectionChangedAction.Remove], listChanges);
        Assert.Same(system, drives.Entries[0]);
    }

    [Fact]
    public void FindsARowByItsMountPointWhateverTheCase()
    {
        var drives = new DriveList(new FakeVolumeInventory().With(@"D:\"), new ManualTimeProvider());

        drives.Refresh();

        Assert.Same(drives.Entries[0], drives.Find(@"d:\"));
        Assert.Null(drives.Find(@"E:\"));
        Assert.Null(drives.Find(null));
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
