using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Each provider's size at each Storage scan (#358): that it round-trips, holds no path, is ignored
/// whole where it cannot be read, keeps one figure a day up to its limit, and keeps only what a scan
/// really measured.
/// </summary>
public sealed class ProviderSizeHistoryTests : IDisposable
{
    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();

    private readonly FakeUserEnvironment _environment;

    private readonly ProviderSizeStore _store;

    public ProviderSizeHistoryTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _store = new ProviderSizeStore(_environment);
    }

    private string File => Path.Combine(_environment.LocalAppData, "Deguffer", "provider-sizes.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheHistoryRoundTrips()
    {
        var history = new ProviderSizeHistory(_store);

        Assert.True(history.Record([Found("npm", 4096), Found("gradle", 8192, approximate: true)], Then, elevated: true));

        var read = new ProviderSizeHistory(new ProviderSizeStore(_environment));
        Assert.Equal(new SizePoint(Then, 4096, Approximate: false, Elevated: true), Assert.Single(read.Of("npm")));
        Assert.Equal(new SizePoint(Then, 8192, Approximate: true, Elevated: true), Assert.Single(read.Of("gradle")));
        Assert.Equal(4096, read.Latest()["npm"].Bytes);
        Assert.True(read.HasAny);
    }

    /// <summary>
    /// The file holds a provider's id, a size, a time and two flags, and nothing that names a place:
    /// no separator and no drive, though the findings it came from are full of both.
    /// </summary>
    [Fact]
    public void TheHistoryHoldsNoPath()
    {
        var history = new ProviderSizeHistory(_store);

        history.Record([Found("npm", 4096)], Then, elevated: false);

        var json = System.IO.File.ReadAllText(File);
        Assert.DoesNotContain("\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testuser", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An entry named by anything but a provider's id, a path above all, is never written.</summary>
    [Theory]
    [InlineData(@"C:\Users\testuser\.gradle")]
    [InlineData("npm/cache")]
    [InlineData("NPM")]
    [InlineData("")]
    public void AnEntryThatIsNotAProviderIdIsRefused(string id)
    {
        IReadOnlyDictionary<string, IReadOnlyList<SizePoint>> history =
            new Dictionary<string, IReadOnlyList<SizePoint>> { [id] = [new SizePoint(Then, 1, false, false)] };

        Assert.Throws<ArgumentException>(() => _store.Save(history));
        Assert.False(System.IO.File.Exists(File));
    }

    /// <summary>Every shipped provider's id is one the store takes, so each row's history can be kept.</summary>
    [Fact]
    public void EveryShippedProviderIdIsOneTheStoreTakes()
    {
        using var shipped = new ShippedProviders();

        Assert.All(shipped.Providers, provider => Assert.True(ProviderSizeStore.IsProviderId(provider.Id), provider.Id));
    }

    /// <summary>Each way a file can be unusable is read as no history at all, never in part.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("torn")]
    [InlineData("future format")]
    [InlineData("path as an id")]
    [InlineData("negative size")]
    [InlineData("out of order")]
    [InlineData("local time")]
    [InlineData("null figures")]
    [InlineData("null figure")]
    [InlineData("unknown member")]
    [InlineData("missing member")]
    public void AnUnreadableFileIsIgnoredRatherThanCompared(string damage)
    {
        new ProviderSizeHistory(_store).Record([Found("npm", 4096), Found("gradle", 2048)], Then, elevated: false);
        var json = System.IO.File.ReadAllText(File);
        var npm = """{"takenUtc":"2026-09-01T09:00:00Z","bytes":4096,"approximate":false,"elevated":false}""";
        Assert.Contains(npm, json, StringComparison.Ordinal);

        var damaged = damage switch
        {
            "not json" => "{ this is not",
            "torn" => json[..(json.Length / 2)],
            "future format" => json.Replace("\"format\":1", "\"format\":2", StringComparison.Ordinal),
            "path as an id" => json.Replace("\"npm\"", "\"C:\\\\Users\\\\testuser\\\\npm\"", StringComparison.Ordinal),
            "negative size" => json.Replace("\"bytes\":4096", "\"bytes\":-4096", StringComparison.Ordinal),
            "out of order" => json.Replace(npm, npm + "," + npm.Replace("09:00:00Z", "08:00:00Z", StringComparison.Ordinal), StringComparison.Ordinal),
            "local time" => json.Replace("09:00:00Z", "09:00:00", StringComparison.Ordinal),
            "null figures" => json.Replace("[" + npm + "]", "null", StringComparison.Ordinal),
            "null figure" => json.Replace("[" + npm + "]", "[null]", StringComparison.Ordinal),
            "unknown member" => json.Replace("\"bytes\":4096", "\"bytes\":4096,\"path\":\"x\"", StringComparison.Ordinal),
            "missing member" => json.Replace("\"bytes\":4096,", string.Empty, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        Assert.NotEqual(json, damaged);
        System.IO.File.WriteAllText(File, damaged);

        var read = new ProviderSizeHistory(new ProviderSizeStore(_environment));

        Assert.Empty(read.Latest());
        Assert.Empty(read.Of("gradle"));
        Assert.False(read.HasAny);
    }

    /// <summary>
    /// A second scan the same day replaces the first one's figure, so a day of scans and cleans costs
    /// one figure, and the latest is what the next day compares with.
    /// </summary>
    [Fact]
    public void OneFigureIsKeptADayTheLatest()
    {
        var history = new ProviderSizeHistory(_store);

        history.Record([Found("npm", 100)], Then, elevated: false);
        history.Record([Found("npm", 300)], Then.AddHours(5), elevated: false);
        history.Record([Found("npm", 500)], Then.AddDays(1), elevated: false);

        Assert.Equal([300L, 500L], history.Of("npm").Select(point => point.Bytes));
    }

    /// <summary>Past the limit the oldest figure goes first.</summary>
    [Fact]
    public void TheOldestFigureGoesPastTheLimit()
    {
        var history = new ProviderSizeHistory(_store);

        for (var day = 0; day <= ProviderSizeStore.KeptPerProvider; day++)
        {
            history.Record([Found("npm", day)], Then.AddDays(day), elevated: false);
        }

        var kept = new ProviderSizeHistory(new ProviderSizeStore(_environment)).Of("npm");
        Assert.Equal(ProviderSizeStore.KeptPerProvider, kept.Count);
        Assert.Equal(1, kept[0].Bytes);
        Assert.Equal(ProviderSizeStore.KeptPerProvider, kept[^1].Bytes);
    }

    /// <summary>
    /// A scan of a few chosen locations adds their figures and leaves every other provider's as it was.
    /// </summary>
    [Fact]
    public void AScanOfSomeLocationsLeavesTheOthersHistory()
    {
        var history = new ProviderSizeHistory(_store);

        history.Record([Found("npm", 100), Found("gradle", 200)], Then, elevated: false);
        history.Record([Found("npm", 150)], Then.AddDays(1), elevated: false);

        Assert.Equal(2, history.Of("npm").Count);
        Assert.Equal(200, Assert.Single(history.Of("gradle")).Bytes);
    }

    /// <summary>
    /// A zero that is not a size is not kept as one: a tool that is not installed, a provider with
    /// nowhere approved to look, a location not examined and one that could not be listed. Kept, each
    /// would read as the cause having been emptied.
    /// </summary>
    [Fact]
    public void OnlyWhatAScanMeasuredIsKept()
    {
        var history = new ProviderSizeHistory(_store);

        var recorded = history.Record(
            [
                new Finding(new FakeCleanupProvider("absent"), IsPresent: false, Plan: null),
                Found("awaiting", 0) with { AwaitingSourceFolders = true },
                Found("unexamined", 0, plan => plan with { WasNotExamined = true }),
                Found("unreadable", 0, plan => plan with { HasUnreadableRoot = true }),
            ],
            Then,
            elevated: false);

        Assert.False(recorded);
        Assert.False(history.HasAny);
        Assert.False(System.IO.File.Exists(File));

        history.Record([Found("clear", 0)], Then, elevated: false);

        Assert.Equal(0, Assert.Single(history.Of("clear")).Bytes);
    }

    [Fact]
    public void RemovingTheHistoryRemovesItAndSaysSo()
    {
        var history = new ProviderSizeHistory(_store);
        history.Record([Found("npm", 100)], Then, elevated: false);
        var raised = 0;
        history.Changed += (_, _) => raised++;

        Assert.True(history.RemoveAll());

        Assert.False(System.IO.File.Exists(File));
        Assert.False(history.HasAny);
        Assert.Equal(1, raised);
        Assert.True(history.RemoveAll());
    }

    /// <summary>A change is signed, and approximate where either figure was or the two scans held different rights.</summary>
    [Fact]
    public void AChangeIsSignedAndSaysWhenItIsApproximate()
    {
        static string Date(DateTime utc) => utc.ToString("yyyy-MM-dd");
        var earlier = new SizePoint(Then, 1L << 30, Approximate: false, Elevated: false);

        Assert.Null(SizeChange.Between(null, earlier));
        Assert.Equal(
            "+1.0 GB since 2026-09-01",
            SizeChange.Between(earlier, earlier with { TakenUtc = Then.AddDays(1), Bytes = 2L << 30 })!.Describe(Date));
        Assert.Equal(
            "\u22121.0 GB since 2026-09-01",
            SizeChange.Between(earlier, earlier with { Bytes = 0 })!.Describe(Date));
        Assert.Equal("No change since 2026-09-01", SizeChange.Between(earlier, earlier)!.Describe(Date));
        Assert.Equal(
            "About +1.0 GB since 2026-09-01",
            SizeChange.Between(earlier, earlier with { Bytes = 2L << 30, Elevated = true })!.Describe(Date));
        Assert.Equal(
            "About +1.0 GB since 2026-09-01",
            SizeChange.Between(earlier, earlier with { Bytes = 2L << 30, Approximate = true })!.Describe(Date));
    }

    /// <summary>A present provider's finding, with one step of <paramref name="bytes"/>.</summary>
    private static Finding Found(string id, long bytes, bool approximate = false) =>
        Found(id, bytes, plan => plan, approximate);

    private static Finding Found(string id, long bytes, Func<CleanupPlan, CleanupPlan> shape, bool approximate = false) =>
        new(
            new FakeCleanupProvider(id),
            IsPresent: true,
            shape(new CleanupPlan
            {
                ProviderId = id,
                ProviderName = id,
                Tier = SafetyTier.RegenerableCache,
                WhatHappensOnNextUse = "Rebuilt on next use.",
                Steps =
                [
                    new DeleteDirectoryStep($@"C:\Users\testuser\AppData\Local\{id}", id)
                    {
                        Estimated = ScanSize.FromLengths(bytes) with { IsApproximate = approximate },
                    },
                ],
            }));
}
