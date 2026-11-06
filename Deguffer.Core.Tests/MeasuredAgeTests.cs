using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A measurement dates the directory it measured (<see cref="ScanResult.LastWritten"/>), and every
/// route dates it exactly as <see cref="DirectoryAge.Of(string, CancellationToken)"/> does.
///
/// <para>A row whose age is only shown takes it from the measurement rather than listing the target
/// again. The age column then reads whichever route measured the row, so a route that dated a
/// directory by a different rule would change the column with the route, and the direction that
/// matters is the one that reads older than the truth.</para>
/// </summary>
public sealed class MeasuredAgeTests : IDisposable
{
    private static readonly DateTime LongAgo = new(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Walks => ["walk", "links"];

    private static IDirectoryScanner Walk(string route) =>
        route == "walk" ? ParallelEnumerationScanner.Default : HardLinkAwareScanner.Default;

    /// <summary>
    /// The directory's own time where it is the newest, an entry's where that is, the time an entry
    /// arrived where it kept an older last write, and the top level only where the newest file is two
    /// levels down. All in one measurement, so a root handed another's age fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(Walks))]
    public async Task EachWalkDatesADirectoryAsDirectoryAgeDoes(string route)
    {
        var ownNewest = Tree("own-newest");
        EntryTime.Set(Path.Combine(ownNewest, "kept.dll"), LongAgo);
        EntryTime.Set(ownNewest, created: LongAgo, written: LongAgo.AddDays(30));

        var entryNewest = Tree("entry-newest");
        EntryTime.Set(Path.Combine(entryNewest, "kept.dll"), created: LongAgo, written: LongAgo.AddDays(20));
        EntryTime.Set(entryNewest, LongAgo);

        var copiedIn = Tree("copied-in");
        EntryTime.Set(Path.Combine(copiedIn, "kept.dll"), created: LongAgo.AddDays(10), written: LongAgo);
        EntryTime.Set(copiedIn, LongAgo);

        var deep = Tree("deep");
        var below = _temp.CreateDirectory("deep", "Debug");
        var deepest = _temp.CreateFile(16, "deep", "Debug", "Example.dll");
        EntryTime.Set(deepest, LongAgo.AddDays(400));
        EntryTime.Set(below, LongAgo.AddDays(5));
        EntryTime.Set(Path.Combine(deep, "kept.dll"), LongAgo);
        EntryTime.Set(deep, LongAgo);

        string[] paths = [ownNewest, entryNewest, copiedIn, deep];
        var measured = await Walk(route).MeasureManyAsync([.. paths.Select(path => new ScanRequest(path))]);

        Assert.Equal(
            [LongAgo.AddDays(30), LongAgo.AddDays(20), LongAgo.AddDays(10), LongAgo.AddDays(5)],
            measured.Select(result => result.LastWritten));

        Assert.Equal(paths.Select(path => DirectoryAge.Of(path)), measured.Select(result => result.LastWritten));
    }

    /// <summary>
    /// What a removal leaves standing still dates the directory: an entry its bounds leave out, and a
    /// mail store, which no figure counts. The age describes the directory, not the part a removal
    /// takes, so it is the age <see cref="DirectoryAge"/> gives either way.
    /// </summary>
    [Theory]
    [MemberData(nameof(Walks))]
    public async Task EntriesTheFigureLeavesOutStillDateTheDirectory(string route)
    {
        var spared = Tree("spared");
        var inUse = _temp.CreateFile(16, "spared", "in-use.lock");
        EntryTime.Set(inUse, LongAgo.AddDays(50));
        EntryTime.Set(Path.Combine(spared, "kept.dll"), LongAgo);
        EntryTime.Set(spared, LongAgo);

        var mail = Tree("mail");
        var store = _temp.CreateFile(16, "mail", "archive.pst");
        EntryTime.Set(store, LongAgo.AddDays(60));
        EntryTime.Set(Path.Combine(mail, "kept.dll"), LongAgo);
        EntryTime.Set(mail, LongAgo);

        var measured = await Walk(route).MeasureManyAsync(
        [
            new ScanRequest(spared, new ScanBounds(KeepRoot: true, [inUse])),
            new ScanRequest(mail),
        ]);

        Assert.Equal([LongAgo.AddDays(50), LongAgo.AddDays(60)], measured.Select(result => result.LastWritten));
        Assert.Equal([DirectoryAge.Of(spared), DirectoryAge.Of(mail)], measured.Select(result => result.LastWritten));
    }

    /// <summary>
    /// A path inside another asked about in the same measurement is read off the outer walk, and is
    /// dated by its own listing there rather than by the outer path's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Walks))]
    public async Task APathInsideAnotherIsDatedByItsOwnListing(string route)
    {
        var outer = Tree("outer");
        var inner = _temp.CreateDirectory("outer", "inner");
        var newest = _temp.CreateFile(16, "outer", "inner", "fresh.dll");
        EntryTime.Set(newest, LongAgo.AddDays(70));
        EntryTime.Set(inner, LongAgo.AddDays(3));
        EntryTime.Set(Path.Combine(outer, "kept.dll"), LongAgo);
        EntryTime.Set(outer, LongAgo);

        var measured = await Walk(route).MeasureManyAsync([new ScanRequest(outer), new ScanRequest(inner)]);

        Assert.Equal([LongAgo.AddDays(3), LongAgo.AddDays(70)], measured.Select(result => result.LastWritten));
    }

    /// <summary>
    /// No age where the path is not a directory listed to the end, as <see cref="DirectoryAge"/>
    /// answers: a refused listing leaves only the half that reads older than the truth, a missing
    /// directory has nothing to date, and a file is not dated one level down.
    /// </summary>
    [Theory]
    [MemberData(nameof(Walks))]
    public async Task NoAgeWhereTheDirectoryIsNotListedToTheEnd(string route)
    {
        var refused = Tree("refused");
        EntryTime.Set(Path.Combine(refused, "kept.dll"), LongAgo.AddDays(9));
        EntryTime.Set(refused, LongAgo);

        var missing = Path.Combine(_temp.Path, "never-existed");
        var file = _temp.CreateFile(16, "MEMORY.DMP");

        using var denied = new DeniedDirectory(refused);

        var measured = await Walk(route).MeasureManyAsync(
            [new ScanRequest(refused), new ScanRequest(missing), new ScanRequest(file)]);

        Assert.All(measured, result => Assert.Null(result.LastWritten));
        Assert.Equal(RootReach.NotListed, measured[0].Root);
        Assert.Null(DirectoryAge.Of(refused));
    }

    /// <summary>
    /// The volume index dates a directory by the same rule from the records it holds: its own record,
    /// and each child's, a file with two names in the directory counted by its record under both, and a
    /// subdirectory by its own record rather than by anything below it.
    /// </summary>
    [Fact]
    public async Task TheVolumeIndexDatesADirectoryAsTheWalkDoes()
    {
        var volume = Volume()
            .AddDirectory(30, Profile, "own-newest", created: LongAgo, lastWritten: LongAgo.AddDays(30))
            .AddFile(31, 30, "kept.dll", 16, 16, created: LongAgo, lastWritten: LongAgo)
            .AddDirectory(40, Profile, "entry-newest", created: LongAgo, lastWritten: LongAgo)
            .AddHardLinkedFile(41, [(40, "a.dll"), (40, "b.dll")], 16, 16, created: LongAgo, lastWritten: LongAgo.AddDays(20))
            .AddDirectory(50, Profile, "copied-in", created: LongAgo, lastWritten: LongAgo)
            .AddFile(51, 50, "kept.dll", 16, 16, created: LongAgo.AddDays(10), lastWritten: LongAgo)
            .AddDirectory(60, Profile, "deep", created: LongAgo, lastWritten: LongAgo)
            .AddDirectory(61, 60, "Debug", created: LongAgo, lastWritten: LongAgo.AddDays(5))
            .AddFile(62, 61, "Example.dll", 16, 16, created: LongAgo, lastWritten: LongAgo.AddDays(400));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', volume), tuning: RouteTuners.Table);

        var measured = await scanner.MeasureManyAsync(
            [.. new[] { "own-newest", "entry-newest", "copied-in", "deep" }.Select(name => new ScanRequest(@"C:\Users\testuser\" + name))]);

        Assert.All(measured, result => Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy));
        Assert.Equal(
            [LongAgo.AddDays(30), LongAgo.AddDays(20), LongAgo.AddDays(10), LongAgo.AddDays(5)],
            measured.Select(result => result.LastWritten));
    }

    /// <summary>
    /// A record whose times the table never read makes the age unknown rather than older than the
    /// truth, as every route answers for a time that was never set (<see cref="OneLevelAge"/>). The
    /// size is still the table's.
    /// </summary>
    [Fact]
    public async Task AnUndatedRecordLeavesTheVolumeIndexWithNoAge()
    {
        var volume = Volume()
            .AddDirectory(30, Profile, "cache", created: LongAgo, lastWritten: LongAgo)
            .AddFile(31, 30, "dated.bin", 16, 16, created: LongAgo, lastWritten: LongAgo.AddDays(1))
            .AddFileWithNoTimestamps(32, 30, "undated.bin", 16);

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving('C', volume), tuning: RouteTuners.Table);

        var measured = await scanner.MeasureAsync(@"C:\Users\testuser\cache");

        Assert.Equal(ScanStrategy.MasterFileTable, measured.Strategy);
        Assert.Null(measured.LastWritten);
    }

    private const uint Users = 6;
    private const uint Profile = 7;

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser");

    /// <summary>A directory holding one file, both left for the test to date.</summary>
    private string Tree(string name)
    {
        var directory = _temp.CreateDirectory(name);
        _temp.CreateFile(16, name, "kept.dll");

        return directory;
    }
}
