using Deguffer.Core.Cloud;
using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Sync roots put into walks by disk (#1071): the roots on one disk in one walk that shares its
/// threads, roots on separate disks walked at once, and roots on a disk not known in one set with
/// every other.
///
/// <para>Every root holds loose files and no folder, so a walk of one root alone describes one
/// placeholder at a time, and more than one at once is only possible where roots were read together.
/// A held description waits for one more than the walks may allow, so overlap they should not allow has
/// the time to show.</para>
/// </summary>
public sealed class PlaceholderWalksTests
{
    /// <summary>How long a description that must have company waits for it before the test fails (#395).</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a description waits for company that would prove the walks read more at once than they
    /// may. Paid in full by each of the first few descriptions when the walks are right.
    /// </summary>
    private static readonly TimeSpan Company = TimeSpan.FromSeconds(1);

    private static FakeCloudFiles Roots(params (string Root, int Files)[] roots)
    {
        var cloud = new FakeCloudFiles();

        foreach (var (root, files) in roots)
        {
            cloud.Root($"OneDrive!{root}", root, Path.GetFileName(root));

            for (var file = 0; file < files; file++)
            {
                cloud.File(Path.Combine(root, $"file {file}.bin"), onDisk: 100 + file);
            }
        }

        return cloud;
    }

    /// <summary>Every kind of drive walked on <paramref name="threads"/> threads, with the volumes and disks given.</summary>
    private static ScanTuner Tuner(int threads, FakeStorageQueries storage, params string[] volumes)
    {
        var inventory = new FakeVolumeInventory();

        foreach (var volume in volumes)
        {
            inventory.With(volume);
        }

        var scanning = Enum.GetValues<StorageMedia>().Aggregate(
            ScanPreferences.Default,
            (preferences, media) => preferences.With(media, new MediaScanPreferences(WalkThreads: threads)));

        return new ScanTuner(
            new FakePreferences(AppPreferences.Default with { Scanning = scanning }), new VolumeMediaCache(storage), inventory);
    }

    /// <summary>
    /// Whether a description is of one of the roots' files. Each root's own pin is read before its walk
    /// starts, one root after another, so a hold there would use up the holds before any walk began.
    /// </summary>
    private static bool IsFile(string path) => path.EndsWith(".bin", StringComparison.Ordinal);

    private static Task<IReadOnlyList<ReleaseSelection>> WalkAsync(ICloudFiles cloud, ScanTuner tuner, params string[] roots) =>
        PlaceholderWalks.OfAsync(cloud, roots, MinimumAge.Off, tuner, CancellationToken.None);

    /// <summary>
    /// Three roots on one volume tuned to two threads: two are read at once, because they share one
    /// walk, and never three, because a walk for each would read the volume with more threads than it
    /// is tuned for. A walk of each root in turn would describe one placeholder at a time.
    /// </summary>
    [Fact]
    public async Task RootsOnOneVolumeShareOneWalk()
    {
        var watch = new PlaceholderOverlapWatch(
            Roots((@"X:\One", 4), (@"X:\Two", 4), (@"X:\Three", 4)), IsFile, wanted: 2, Deadline, beyond: Company);
        var tuner = Tuner(2, new FakeStorageQueries().Volume(@"X:\", 1), @"X:\");

        var found = await WalkAsync(watch, tuner, @"X:\One", @"X:\Two", @"X:\Three");

        Assert.Equal(2, watch.MostAtOnce);
        Assert.All(found, selection => Assert.Equal(4, selection.Files.Count));
    }

    [Fact]
    public async Task VolumesOnSeparateDisksAreWalkedAtOnce()
    {
        var watch = new PlaceholderOverlapWatch(
            Roots((@"X:\Personal", 4), (@"Y:\Work", 4)), IsFile, wanted: 2, Deadline);
        var tuner = Tuner(1, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        await WalkAsync(watch, tuner, @"X:\Personal", @"Y:\Work");

        Assert.Equal(2, watch.MostAtOnce);
    }

    /// <summary>
    /// Two volumes of one disk tuned to one thread are read one placeholder at a time: a walk of each at
    /// once would make a spinning disk seek between them for the length of both.
    /// </summary>
    [Fact]
    public async Task VolumesOnOneDiskShareOneWalksThreads()
    {
        var watch = new PlaceholderOverlapWatch(
            Roots((@"X:\Personal", 4), (@"Y:\Work", 4)), IsFile, wanted: 2, Company, holdsAtMost: 2);
        var tuner = Tuner(1, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 1), @"X:\", @"Y:\");

        await WalkAsync(watch, tuner, @"X:\Personal", @"Y:\Work");

        Assert.Equal(1, watch.MostAtOnce);
    }

    /// <summary>A volume that would not say which disk it is on might be on the disk of any other.</summary>
    [Fact]
    public async Task AVolumeOnADiskNotKnownSharesItsThreadsWithTheOthers()
    {
        var watch = new PlaceholderOverlapWatch(
            Roots((@"X:\Personal", 4), (@"Y:\Work", 4)), IsFile, wanted: 2, Company, holdsAtMost: 2);
        var tuner = Tuner(1, new FakeStorageQueries().Volume(@"X:\", 1), @"X:\", @"Y:\");

        await WalkAsync(watch, tuner, @"X:\Personal", @"Y:\Work");

        Assert.Equal(1, watch.MostAtOnce);
    }

    /// <summary>
    /// Roots on two volumes, given out of volume order: each selection is its own root's, in the order
    /// the roots were asked for, whichever walk found it.
    /// </summary>
    [Fact]
    public async Task EachRootsSelectionComesBackInTheOrderAsked()
    {
        var cloud = Roots((@"X:\One", 1), (@"Y:\Two", 2), (@"X:\Three", 3));
        var tuner = Tuner(4, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        var found = await WalkAsync(cloud, tuner, @"X:\Three", @"Y:\Two", @"X:\One");

        Assert.Equal([3, 2, 1], found.Select(selection => selection.Files.Count));
        Assert.All(found[0].Files, f => Assert.True(LongPath.Contains(@"X:\Three", f.Path)));
        Assert.All(found[1].Files, f => Assert.True(LongPath.Contains(@"Y:\Two", f.Path)));
        Assert.All(found[2].Files, f => Assert.True(LongPath.Contains(@"X:\One", f.Path)));
    }

    [Fact]
    public async Task NoRootsWalksNothing()
    {
        var found = await WalkAsync(new FakeCloudFiles(), ScanTuner.Shipped);

        Assert.Empty(found);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var tuner = Tuner(1, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaceholderWalks.OfAsync(
            Roots((@"X:\Personal", 4), (@"Y:\Work", 4)), [@"X:\Personal", @"Y:\Work"], MinimumAge.Off, tuner, cancelled.Token));
    }
}
