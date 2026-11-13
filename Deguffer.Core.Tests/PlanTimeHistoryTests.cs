using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How long each provider took to plan in the recent full passes (#443): kept only from a full pass
/// that ended, as the median of the last few at each elevation, with no path in the file, and read as
/// no history where the file cannot be read.
/// </summary>
public sealed class PlanTimeHistoryTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    private readonly FakeUserEnvironment _environment;

    /// <summary>The clock each kept pass is dated by, moved on an hour before each so passes keep their order.</summary>
    private readonly ManualTimeProvider _clock = new();

    public PlanTimeHistoryTests() => _environment = new FakeUserEnvironment(_temp.Path);

    private string File => Path.Combine(_environment.LocalAppData, "Deguffer", "planning-times.json");

    public void Dispose() => _temp.Dispose();

    private PlanTimeHistory NewHistory() => new(new PlanTimeStore(_environment), _clock);

    [Fact]
    public void AFullPassRoundTrips()
    {
        var npm = new FakeCleanupProvider("npm");
        var conda = new FakeCleanupProvider("conda");

        Assert.True(Record(NewHistory(), [npm, conda], [Took(npm, 120), Took(conda, 5232)], elevated: false));

        var read = NewHistory();
        Assert.Equal(TimeSpan.FromMilliseconds(120), read.MedianOf("npm", elevated: false));
        Assert.Equal(TimeSpan.FromMilliseconds(5232), read.MedianOf("conda", elevated: false));
        Assert.Null(read.MedianOf("npm", elevated: true));
        Assert.Null(read.MedianOf("gradle", elevated: false));
    }

    /// <summary>
    /// A full pass run by a planner is kept: the planner's own times, handed over as the Storage page
    /// hands them, cover every provider.
    /// </summary>
    [Fact]
    public async Task APassThatPlannedEveryProviderIsKept()
    {
        var clock = new ManualTimeProvider();
        var npm = new FakeCleanupProvider("npm") { WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(2)) };
        var planner = new CleanupPlanner([npm], time: clock);
        var timed = new PlanTimeLog();

        await planner.PlanAllAsync(timed: timed).WaitAsync(Deadline);

        Assert.True(Record(NewHistory(), planner.Providers, timed.Times, elevated: true));
        Assert.Equal(TimeSpan.FromSeconds(2), NewHistory().MedianOf("npm", elevated: true));
    }

    /// <summary>
    /// A pass cancelled part-way ended its rows early, and the rows after them never planned, so what it
    /// timed is not kept, and no file is written.
    /// </summary>
    [Fact]
    public async Task ACancelledPassWritesNoHistory()
    {
        using var cancel = new CancellationTokenSource();
        var providers = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce * 4)
            .Select(index => new FakeCleanupProvider($"p{index}"))
            .ToList();
        providers[0].WhilePlanning = cancel.Cancel;
        var planner = new CleanupPlanner(providers);
        var timed = new PlanTimeLog();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => planner.PlanAllAsync(timed: timed, ct: cancel.Token).WaitAsync(Deadline));

        Assert.False(Record(NewHistory(), planner.Providers, timed.Times, elevated: false));
        Assert.False(System.IO.File.Exists(File));
    }

    /// <summary>
    /// The re-plan after a clean plans only the rows the clean changed, against emptied folders, so its
    /// times are not kept, and neither are a scan of the chosen locations'.
    /// </summary>
    [Fact]
    public async Task ARePlanOfSomeProvidersWritesNoHistory()
    {
        var npm = new FakeCleanupProvider("npm");
        var conda = new FakeCleanupProvider("conda");
        var planner = new CleanupPlanner([npm, conda]);
        var timed = new PlanTimeLog();

        await planner.PlanAsync([npm], timed: timed).WaitAsync(Deadline);

        Assert.Single(timed.Times);
        Assert.False(Record(NewHistory(), planner.Providers, timed.Times, elevated: false));
        Assert.False(System.IO.File.Exists(File));
    }

    /// <summary>Times that name a provider twice, or one the planner does not have, are not one full pass.</summary>
    [Fact]
    public void TimesThatAreNotOneForEachProviderAreRefused()
    {
        var npm = new FakeCleanupProvider("npm");
        var conda = new FakeCleanupProvider("conda");
        var stranger = new FakeCleanupProvider("conda");
        var history = NewHistory();

        Assert.False(Record(history, [npm, conda], [Took(npm, 1), Took(npm, 2)], elevated: false));
        Assert.False(Record(history, [npm, conda], [Took(npm, 1), Took(stranger, 2)], elevated: false));
        Assert.False(Record(history, [], [], elevated: false));
        Assert.False(System.IO.File.Exists(File));
    }

    /// <summary>
    /// The median of the newest five at each elevation: one slow pass moves nothing, the oldest goes
    /// past five, and an elevated pass is never averaged with one without the rights.
    /// </summary>
    [Fact]
    public void TheMedianOfTheNewestFewAtEachElevationIsKept()
    {
        var conda = new FakeCleanupProvider("conda");
        var history = NewHistory();
        int[] unelevated = [9000, 100, 200, 300, 400, 5000];

        for (var pass = 0; pass < unelevated.Length; pass++)
        {
            Assert.True(Record(history, [conda], [Took(conda, unelevated[pass])], elevated: false));
        }

        Assert.True(Record(history, [conda], [Took(conda, 60_000)], elevated: true));

        var read = NewHistory();

        // The 9,000 ms pass was the oldest of six, so the five kept are 100 to 400 and 5,000.
        Assert.Equal(TimeSpan.FromMilliseconds(300), read.MedianOf("conda", elevated: false));
        Assert.Equal(TimeSpan.FromMilliseconds(60_000), read.MedianOf("conda", elevated: true));

        Record(history, [conda], [Took(conda, 50)], elevated: false);
        Assert.Equal(TimeSpan.FromMilliseconds(300), NewHistory().MedianOf("conda", elevated: false));
    }

    /// <summary>An even count takes the mean of the middle two.</summary>
    [Fact]
    public void AnEvenCountTakesTheMiddleTwo()
    {
        var npm = new FakeCleanupProvider("npm");
        var history = NewHistory();

        Record(history, [npm], [Took(npm, 100)], elevated: false);
        Record(history, [npm], [Took(npm, 300)], elevated: false);

        Assert.Equal(TimeSpan.FromMilliseconds(200), NewHistory().MedianOf("npm", elevated: false));
    }

    /// <summary>The file holds a provider's id, a time and a flag, and nothing that names a place.</summary>
    [Fact]
    public void TheHistoryHoldsNoPath()
    {
        var npm = new FakeCleanupProvider("npm");

        Record(NewHistory(), [npm], [Took(npm, 100)], elevated: false);

        var json = System.IO.File.ReadAllText(File);
        Assert.DoesNotContain("\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testuser", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each way a file can be unusable is read as no history at all, never in part.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("torn")]
    [InlineData("future format")]
    [InlineData("path as an id")]
    [InlineData("negative time")]
    [InlineData("time past any duration")]
    [InlineData("out of order")]
    [InlineData("local time")]
    [InlineData("null times")]
    [InlineData("null time")]
    [InlineData("unknown member")]
    [InlineData("missing member")]
    public void AnUnreadableFileReadsAsNoHistory(string damage)
    {
        var npm = new FakeCleanupProvider("npm");
        var gradle = new FakeCleanupProvider("gradle");
        Record(NewHistory(), [npm, gradle], [Took(npm, 4), Took(gradle, 2)], elevated: false);
        var json = System.IO.File.ReadAllText(File);
        var time = """{"takenUtc":"2026-06-01T13:00:00Z","microseconds":4000,"elevated":false}""";
        Assert.Contains(time, json, StringComparison.Ordinal);

        var damaged = damage switch
        {
            "not json" => "{ this is not",
            "torn" => json[..(json.Length / 2)],
            "future format" => json.Replace("\"format\":1", "\"format\":2", StringComparison.Ordinal),
            "path as an id" => json.Replace("\"npm\"", "\"C:\\\\Users\\\\testuser\\\\npm\"", StringComparison.Ordinal),
            "negative time" => json.Replace("\"microseconds\":4000", "\"microseconds\":-4000", StringComparison.Ordinal),
            "time past any duration" => json.Replace("\"microseconds\":4000", $"\"microseconds\":{long.MaxValue}", StringComparison.Ordinal),
            "out of order" => json.Replace(time, time + "," + time.Replace("13:00:00Z", "12:00:00Z", StringComparison.Ordinal), StringComparison.Ordinal),
            "local time" => json.Replace("13:00:00Z", "13:00:00", StringComparison.Ordinal),
            "null times" => json.Replace("[" + time + "]", "null", StringComparison.Ordinal),
            "null time" => json.Replace("[" + time + "]", "[null]", StringComparison.Ordinal),
            "unknown member" => json.Replace("\"microseconds\":4000", "\"microseconds\":4000,\"path\":\"x\"", StringComparison.Ordinal),
            "missing member" => json.Replace("\"microseconds\":4000,", string.Empty, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        Assert.NotEqual(json, damaged);
        System.IO.File.WriteAllText(File, damaged);

        var read = NewHistory();

        Assert.Null(read.MedianOf("npm", elevated: false));
        Assert.Null(read.MedianOf("gradle", elevated: false));
    }

    private bool Record(PlanTimeHistory history, IReadOnlyList<ICleanupProvider> providers, IReadOnlyList<ProviderPlanTime> times, bool elevated)
    {
        _clock.Advance(TimeSpan.FromHours(1));
        return history.RecordFullPass(providers, times, elevated);
    }

    private static ProviderPlanTime Took(ICleanupProvider provider, int milliseconds) =>
        new(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(milliseconds));
}
