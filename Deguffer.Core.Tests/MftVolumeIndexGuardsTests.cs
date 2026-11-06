using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The table's answer for several guards from one pass over a subtree. A planning pass asks for the
/// whole of a location and for the part its age guard lets go, and each reading must be the one a
/// measurement under that guard alone gives: a folder one guard keeps standing is still taken by the
/// removal under another, and a figure that let one guard's folders leak into another's would report
/// a removal that differs from what happens.
/// </summary>
public sealed class MftVolumeIndexGuardsTests
{
    // An invented profile's package cache, with an old tree holding one recent file deep inside it.
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Cache = 8;
    private const uint Outer = 9;
    private const uint Middle = 10;
    private const uint Inner = 11;
    private const uint Touched = 12;
    private const uint Linked = 13;

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MinimumAge Guard = MinimumAge.WithinHours(8, Now);
    private static readonly string[] CachePath = ["Users", "testuser", ".npm-cache"];

    private static DateTime Old => Now.AddDays(-40);

    private static DateTime Recent => Now.AddMinutes(-5);

    private static MftFixture Tree() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Cache, Profile, ".npm-cache", created: Old, lastWritten: Old);

    /// <summary>
    /// A recent file three folders down in an old tree, a stale file beside its folder, a stale file
    /// at the top, a folder written a minute ago that holds nothing, and a link to a folder.
    /// </summary>
    private static MftFixture DeepTree() => Tree()
        .AddFile(20, Cache, "old.bin", allocated: 4096, logical: 100, created: Old, lastWritten: Old)
        .AddDirectory(Outer, Cache, "outer", created: Old, lastWritten: Old)
        .AddDirectory(Middle, Outer, "middle", created: Old, lastWritten: Old)
        .AddDirectory(Inner, Middle, "inner", created: Old, lastWritten: Old)
        .AddFile(21, Inner, "recent.bin", allocated: 8192, logical: 1000, created: Old, lastWritten: Recent)
        .AddFile(22, Middle, "stale.bin", allocated: 1024, logical: 10, created: Old, lastWritten: Old)
        .AddDirectory(Touched, Cache, "touched", created: Now.AddMinutes(-1), lastWritten: Now.AddMinutes(-1))
        .AddDirectoryLink(Linked, Cache, "linked");

    private static MftVolumeIndex Build(MftFixture fixture)
    {
        using var source = fixture.Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));
        return index;
    }

    /// <summary>What a measurement under <paramref name="keep"/> alone reads.</summary>
    private static MftReading Alone(MftVolumeIndex index, MinimumAge keep, bool keepRoot, IReadOnlySet<uint> leftOut)
    {
        var size = index.TryMeasure(CachePath, keep, keepRoot, leftOut, out var withheld, out _);

        return new MftReading(size!.Value, withheld);
    }

    /// <summary>A describer for a measurement that leaves out folders alone, which are placed by name.</summary>
    private static FileReading NoFileIsDescribed(IReadOnlyList<string> components) =>
        throw new InvalidOperationException($"{string.Join('\\', components)} was described, and only a file should be.");

    /// <summary>
    /// The recent file keeps inner, middle, outer and the cache standing under the guard, and under
    /// the guard alone: with the guard off, every folder goes with what it holds. The folder written a
    /// minute ago is taken under both, because a folder is never kept by its own time.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsTheFoldersAboveARecentFileStandingUnderTheGuardAlone(bool keepRoot)
    {
        var index = Build(DeepTree());

        var measured = index.TryMeasure(CachePath, [MinimumAge.Off, Guard], keepRoot, MftVolumeIndex.NothingLeftOut)!;

        // Off: three files and the folder link, and outer, middle, inner, touched and the cache itself
        // unless it is emptied in place.
        Assert.Equal(
            new MftReading(new ScanSize(4096 + 8192 + 1024, 100 + 1000 + 10, Entries: keepRoot ? 8 : 9), WithheldRecent: false),
            measured.Readings[0]);

        // The guard: two stale files, the link and touched. Every folder above recent.bin stays.
        Assert.Equal(
            new MftReading(new ScanSize(4096 + 1024, 100 + 10, Entries: 4), WithheldRecent: true),
            measured.Readings[1]);

        Assert.Empty(measured.MailStores);
    }

    /// <summary>
    /// Each reading of one pass equals the measurement under its guard alone, in either order, so a
    /// guard's place in the question changes nothing it reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsEachGuardAsAMeasurementUnderThatGuardAloneWould(bool keepRoot)
    {
        var index = Build(DeepTree());
        var none = MftVolumeIndex.NothingLeftOut;

        var offFirst = index.TryMeasure(CachePath, [MinimumAge.Off, Guard], keepRoot, none)!;
        var guardFirst = index.TryMeasure(CachePath, [Guard, MinimumAge.Off], keepRoot, none)!;

        Assert.Equal(Alone(index, MinimumAge.Off, keepRoot, none), offFirst.Readings[0]);
        Assert.Equal(Alone(index, Guard, keepRoot, none), offFirst.Readings[1]);
        Assert.Equal(offFirst.Readings[1], guardFirst.Readings[0]);
        Assert.Equal(offFirst.Readings[0], guardFirst.Readings[1]);
    }

    /// <summary>
    /// Three guards, which keep both files, the newer one alone, and nothing: the folders each guard
    /// keeps standing are its own, and a folder another guard keeps is still counted.
    ///
    /// <para>Both orders of the two folders, because the climb from a kept file stops where its guards
    /// are already marked. Where the file one guard keeps is met after the file two guards keep, the
    /// cache already stands under both. Where it is met first, the cache stands under one guard
    /// alone, and the second file's climb has to go on to mark the other.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsEachGuardsFoldersApartWhereTheGuardsKeepDifferentFiles(bool dayOldFirst)
    {
        var (dayOldFolder, recentFolder) = dayOldFirst ? (Middle, Outer) : (Outer, Middle);

        var index = Build(Tree()
            .AddDirectory(Outer, Cache, dayOldFirst ? "recent" : "day-old", created: Old, lastWritten: Old)
            .AddDirectory(Middle, Cache, dayOldFirst ? "day-old" : "recent", created: Old, lastWritten: Old)
            .AddFile(20, dayOldFolder, "day-old.bin", allocated: 100, logical: 100, created: Old, lastWritten: Now.AddDays(-1))
            .AddFile(21, recentFolder, "recent.bin", allocated: 200, logical: 200, created: Old, lastWritten: Recent));

        var week = MinimumAge.Within(TimeSpan.FromDays(7), Now);

        var measured = index.TryMeasure(CachePath, [week, Guard, MinimumAge.Off], keepRoot: false, MftVolumeIndex.NothingLeftOut)!;

        // A week keeps both files, so every folder stays.
        Assert.Equal(new MftReading(new ScanSize(0, 0, Entries: 0), WithheldRecent: true), measured.Readings[0]);

        // Eight hours keeps recent.bin alone: day-old.bin and its folder go, recent and the cache stay.
        Assert.Equal(new MftReading(new ScanSize(100, 100, Entries: 2), WithheldRecent: true), measured.Readings[1]);

        // Nothing kept: both files, both folders and the cache.
        Assert.Equal(new MftReading(new ScanSize(300, 300, Entries: 5), WithheldRecent: false), measured.Readings[2]);

        Assert.Equal(Alone(index, week, keepRoot: false, MftVolumeIndex.NothingLeftOut), measured.Readings[0]);
        Assert.Equal(Alone(index, Guard, keepRoot: false, MftVolumeIndex.NothingLeftOut), measured.Readings[1]);
    }

    /// <summary>
    /// A mail store and an entry left out stay under every guard, so each keeps its folders standing
    /// in every reading. The store is named once, for all of them (§9).
    /// </summary>
    [Fact]
    public void KeepsAMailStoreAndAnEntryLeftOutStandingUnderEveryGuard()
    {
        const uint Mail = 14;
        const uint Skipped = 15;

        var index = Build(Tree()
            .AddFile(20, Cache, "old.bin", allocated: 100, logical: 100, created: Old, lastWritten: Old)
            .AddFile(21, Cache, "recent.bin", allocated: 200, logical: 200, created: Old, lastWritten: Recent)
            .AddDirectory(Mail, Cache, "mail", created: Old, lastWritten: Old)
            .AddFile(22, Mail, "archive.pst", allocated: 1_048_576, logical: 1_000_000, created: Old, lastWritten: Old)
            .AddDirectory(Skipped, Cache, "skipped", created: Old, lastWritten: Old)
            .AddFile(23, Skipped, "kept.bin", allocated: 400, logical: 400, created: Old, lastWritten: Old));

        var leftOut = index.TryPlace(CachePath, [["skipped"]], NoFileIsDescribed)!;

        var measured = index.TryMeasure(CachePath, [MinimumAge.Off, Guard], keepRoot: false, leftOut)!;

        // Off: both files. The cache and mail stay for the store, and skipped is never entered.
        Assert.Equal(new MftReading(new ScanSize(300, 300, Entries: 2), WithheldRecent: false), measured.Readings[0]);

        // The guard: old.bin alone.
        Assert.Equal(new MftReading(new ScanSize(100, 100, Entries: 1), WithheldRecent: true), measured.Readings[1]);

        Assert.Equal([.. CachePath, "mail", "archive.pst"], Assert.Single(measured.MailStores));
        Assert.Equal(Alone(index, MinimumAge.Off, keepRoot: false, leftOut), measured.Readings[0]);
        Assert.Equal(Alone(index, Guard, keepRoot: false, leftOut), measured.Readings[1]);
    }

    /// <summary>
    /// A size the table does not know is unknown under every guard, so the pass declines every
    /// reading and the walk answers them all. A reading for the guard alone would be a figure missing
    /// a file of unknown size, which nothing downstream can tell from a correct one.
    /// </summary>
    [Fact]
    public void DeclinesEveryReadingForASubtreeHoldingASizeTheTableDoesNotKnow()
    {
        var index = Build(DeepTree().AddFileDescribingOnlyALaterExtent(23, Middle, "split.tgz"));

        Assert.Null(index.TryMeasure(CachePath, [MinimumAge.Off, Guard], keepRoot: false, MftVolumeIndex.NothingLeftOut));
        Assert.Null(index.TryMeasure(CachePath, Guard, out _));
    }

    /// <summary>A path through a link is declined for every guard, as it is for one.</summary>
    [Fact]
    public void DeclinesEveryReadingForAPathThroughALink()
    {
        var index = Build(DeepTree());

        Assert.Null(index.TryMeasure([.. CachePath, "linked"], [MinimumAge.Off, Guard], keepRoot: false, MftVolumeIndex.NothingLeftOut));
    }

    /// <summary>A question with no guard has no reading to give, and one with more guards than a folder's mask has bits cannot be tallied.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void RefusesAQuestionWithNoGuardOrMoreThanAFolderCanMark(int guards)
    {
        var index = Build(DeepTree());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.TryMeasure(CachePath, [.. Enumerable.Repeat(Guard, guards)], keepRoot: false, MftVolumeIndex.NothingLeftOut));
    }

    /// <summary>The largest question a folder's mask can mark is answered, every reading alike.</summary>
    [Fact]
    public void AnswersAQuestionWithAsManyGuardsAsAFolderCanMark()
    {
        var index = Build(DeepTree());

        var measured = index.TryMeasure(CachePath, [.. Enumerable.Repeat(Guard, 32)], keepRoot: false, MftVolumeIndex.NothingLeftOut)!;

        Assert.All(measured.Readings, reading => Assert.Equal(Alone(index, Guard, keepRoot: false, MftVolumeIndex.NothingLeftOut), reading));
    }
}
