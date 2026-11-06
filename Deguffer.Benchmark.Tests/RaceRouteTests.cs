using System.Collections.Concurrent;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The race the benchmark times is a race: the walk and the build raced run at the same time, each
/// is also timed alone, the order alternates every round, and the command given runs before
/// every part. The parts here are fakes that record what ran, so nothing reads this machine's drives.
/// </summary>
public sealed class RaceRouteTests : IDisposable
{
    private const char Drive = 'C';

    /// <summary>Long enough that a part only waits it out where the part it waits for never runs beside it.</summary>
    private static readonly TimeSpan Meeting = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The second walk and the second build of a first round are the raced pair. Each waits for the
    /// other to start: run one after the other, the first waits out <see cref="Meeting"/> alone.
    /// </summary>
    [Fact]
    public void TheRacedWalkAndBuildRunAtTheSameTime()
    {
        using var walking = new ManualResetEventSlim();
        using var building = new ManualResetEventSlim();
        var walks = 0;
        var builds = 0;
        var walkMetBuild = false;
        var buildMetWalk = false;

        var route = new RaceRoute(
            Route.Walk,
            _ =>
            {
                if (Interlocked.Increment(ref walks) == 2)
                {
                    walking.Set();
                    walkMetBuild = building.Wait(Meeting);
                }

                return Tally();
            },
            _ =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    building.Set();
                    buildMetWalk = walking.Wait(Meeting);
                }

                return Tally();
            },
            beforePart: null);

        var tally = route.Run(CancellationToken.None);

        Assert.False(tally.Race!.Reversed);
        Assert.True(walkMetBuild);
        Assert.True(buildMetWalk);
    }

    /// <summary>
    /// The first round walks alone, builds alone, then races. The second races first and walks alone
    /// last, so a cache the first part warms favours each walk in turn.
    /// </summary>
    [Fact]
    public void EveryOtherRoundRacesFirstAndWalksAloneLast()
    {
        var log = new ConcurrentQueue<string>();
        var route = Logging(log, beforePart: null);

        var first = route.Run(CancellationToken.None);
        var firstParts = Drain(log);
        var second = route.Run(CancellationToken.None);
        var secondParts = Drain(log);

        Assert.False(first.Race!.Reversed);
        Assert.Equal(new[] { "walk", "build" }, firstParts[..2]);
        Assert.Equal(new[] { "build", "walk" }, firstParts[2..].Order().ToArray());

        Assert.True(second.Race!.Reversed);
        Assert.Equal(new[] { "build", "walk" }, secondParts[..2].Order().ToArray());
        Assert.Equal(new[] { "build", "walk" }, secondParts[2..]);
    }

    /// <summary>
    /// The command given runs before the walk alone, before the build alone and before the
    /// raced pair, and never between the two raced parts, which start together.
    /// </summary>
    [Fact]
    public void TheCommandGivenRunsBeforeEveryPart()
    {
        var log = new ConcurrentQueue<string>();
        var route = Logging(log, () => log.Enqueue("command"));

        route.Run(CancellationToken.None);
        var parts = Drain(log);

        Assert.Equal(new[] { "command", "walk", "command", "build", "command" }, parts[..5]);
        Assert.Equal(new[] { "build", "walk" }, parts[5..].Order().ToArray());
    }

    [Fact]
    public void WithNoCommandNothingRunsBetweenParts()
    {
        var log = new ConcurrentQueue<string>();

        Logging(log, beforePart: null).Run(CancellationToken.None);

        Assert.DoesNotContain("command", log);
    }

    /// <summary>A part that answered for less than everything leaves the whole round incomplete.</summary>
    [Fact]
    public void AnIncompletePartLeavesTheRoundIncomplete()
    {
        var builds = 0;
        var route = new RaceRoute(
            Route.Walk,
            _ => Tally(),
            _ => Tally(complete: Interlocked.Increment(ref builds) != 2),
            beforePart: null);

        var tally = route.Run(CancellationToken.None);

        Assert.True(tally.Race!.BuildAlone.Complete);
        Assert.False(tally.Race.BuildRaced.Complete);
        Assert.False(tally.Complete);
    }

    /// <summary>
    /// On a folder and a fixture table: the walk counts the folder's entries each time, the build reads
    /// every record each time, and the round is reported as the walk alone's count.
    /// </summary>
    [Fact]
    public void RacesTheWalkOfAFolderAgainstTheBuildOfAVolume()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(10, "root", "a", "one.bin");
        _temp.CreateFile(10, "root", "two.bin");

        var volume = new MftFixture()
            .AddDirectory(16, 5, "folder")
            .AddFile(17, 16, "file.bin", allocated: 4096, logical: 4000)
            .WithoutBitmap();

        var route = RaceRoute.On(
            FakeMftSourceFactory.Serving(Drive, volume), root, Route.Walk, WalkTuning.Default, Drive, TableTuning.Default, beforePart: null);

        var tally = route.Run(CancellationToken.None);
        var race = tally.Race!;

        // a, one.bin in it, and two.bin.
        Assert.Equal(3, tally.Items);
        Assert.Equal(3, race.WalkAlone.Items);
        Assert.Equal(3, race.WalkRaced.Items);
        Assert.Equal(18, race.BuildAlone.Items);
        Assert.Equal(18, race.BuildRaced.Items);
        Assert.Equal(Route.Walk, race.Racer);
        Assert.True(tally.Complete);
    }

    /// <summary>The command given on the command line runs before each of the round's three parts.</summary>
    [Fact]
    public void OnAFolderTheCommandGivenRunsBeforeEachPart()
    {
        var root = _temp.CreateDirectory("root");
        var marker = Path.Combine(_temp.CreateDirectory("marks"), "parts.txt");

        var route = RaceRoute.On(
            FakeMftSourceFactory.Serving(Drive, new MftFixture().WithoutBitmap()),
            root,
            Route.Walk,
            WalkTuning.Default,
            Drive,
            TableTuning.Default,
            $"echo part>> \"{marker}\"");

        route.Run(CancellationToken.None);

        Assert.Equal(3, File.ReadAllLines(marker).Length);
    }

    private static RaceRoute Logging(ConcurrentQueue<string> log, Action? beforePart) =>
        new(
            Route.Walk,
            _ =>
            {
                log.Enqueue("walk");
                return Tally();
            },
            _ =>
            {
                log.Enqueue("build");
                return Tally();
            },
            beforePart);

    private static string[] Drain(ConcurrentQueue<string> log)
    {
        var parts = log.ToArray();
        log.Clear();
        return parts;
    }

    private static RunTally Tally(bool complete = true) => new(1, BytesRead: 0, complete);
}
