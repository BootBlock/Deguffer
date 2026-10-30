using Deguffer.Core.Exploring.History;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// When a drive fills at the rate its kept scans rose (#358): the rate over the run since the last
/// fall, and each case that gives no forecast, with its reason.
/// </summary>
public sealed class FillForecastTests
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Gigabyte = 1L << 30;

    private static readonly DateTime Start = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Words a date as an invariant day, so the sentences can be asserted whole.</summary>
    private static readonly Func<DateTime, string> Date = utc => utc.ToString("yyyy-MM-dd");

    /// <summary>
    /// A gigabyte a day, every week for three weeks, on a drive with 79 GB left at the last: it fills
    /// 79 days after the last scan, which is inside the twelve weeks three weeks of scans speak for.
    /// </summary>
    [Fact]
    public void ASteadyRiseGivesTheDateItFillsOn()
    {
        var forecast = FillForecast.From(Scans(600, (0, 500), (7, 507), (14, 514), (21, 521)));

        Assert.Equal(NoForecast.None, forecast.Why);
        Assert.Equal(Gigabyte, forecast.BytesPerDay, precision: 0);
        Assert.Equal(79, forecast.FillsIn.TotalDays, precision: 6);
        Assert.Equal(Start, forecast.SinceUtc);
        Assert.Equal(4, forecast.Scans);
        Assert.Equal(
            "At the rate since 2026-09-01, measured over 4 scans, this drive fills in about 11 weeks.",
            ForecastText.Of(forecast, Date));
    }

    /// <summary>
    /// A clean takes 60 GB off a drive rising a gigabyte a day. Fitted across the step the rate is a
    /// small fraction of that, so the run starts at the scan after the fall and the rate is the rise
    /// either side of it.
    /// </summary>
    [Fact]
    public void AStepDownFromACleanDoesNotLowerTheRate()
    {
        var kept = Scans(520, (0, 500), (7, 507), (14, 514), (15, 455), (22, 462), (29, 469));

        var forecast = FillForecast.From(kept);

        Assert.Equal(NoForecast.None, forecast.Why);
        Assert.Equal(ForecastBreak.Fell, forecast.Break);
        Assert.Equal(Start.AddDays(15), forecast.SinceUtc);
        Assert.Equal(3, forecast.Scans);
        Assert.Equal(Gigabyte, forecast.BytesPerDay, precision: 0);
        Assert.Equal(51, forecast.FillsIn.TotalDays, precision: 6);
    }

    /// <summary>A fall smaller than churn, a few hundred megabytes, is fitted through rather than starting a run.</summary>
    [Fact]
    public void AFallSmallerThanChurnDoesNotEndTheRun()
    {
        var kept = Scans(600, (0, 500), (7, 507), (8, 506.5), (14, 514), (21, 521));

        var forecast = FillForecast.From(kept);

        Assert.Equal(ForecastBreak.None, forecast.Break);
        Assert.Equal(5, forecast.Scans);
        Assert.Equal(NoForecast.None, forecast.Why);
    }

    /// <summary>Two scans make a line whatever they say, so they give no forecast, and say how many it takes.</summary>
    [Fact]
    public void TooFewScansGiveNoForecastWithTheReason()
    {
        var forecast = FillForecast.From(Scans(600, (0, 500), (14, 514)));

        Assert.Equal(NoForecast.TooFewScans, forecast.Why);
        Assert.Equal(
            "Too few scans of this drive to say when it fills. It takes 3, over a week or more.",
            ForecastText.Of(forecast, Date));
        Assert.Equal(NoForecast.TooFewScans, FillForecast.From([]).Why);
    }

    /// <summary>A fall that leaves too few scans after it says that the fall is why.</summary>
    [Fact]
    public void TooFewScansSinceAFallSayTheFallIsWhy()
    {
        var forecast = FillForecast.From(Scans(600, (0, 500), (7, 507), (14, 514), (21, 450), (28, 457)));

        Assert.Equal(NoForecast.TooFewScans, forecast.Why);
        Assert.Equal(ForecastBreak.Fell, forecast.Break);
        Assert.Equal(
            "Used space fell at the scan of 2026-09-22, and too few scans since then show a rate to say when this drive fills.",
            ForecastText.Of(forecast, Date));
    }

    /// <summary>A drive that changed size starts a run of its own, because its earlier used space was of another capacity.</summary>
    [Fact]
    public void AResizedDriveStartsARunOfItsOwn()
    {
        var kept = Scans(600, (0, 500), (7, 507), (14, 514)).Concat(Scans(800, (21, 521), (28, 528))).ToList();

        var forecast = FillForecast.From(kept);

        Assert.Equal(NoForecast.TooFewScans, forecast.Why);
        Assert.Equal(ForecastBreak.Resized, forecast.Break);
        Assert.StartsWith("This drive changed size at the scan of 2026-09-22", ForecastText.Of(forecast, Date));
    }

    /// <summary>Scans over less than a week measure a download, not a trend.</summary>
    [Fact]
    public void ScansOverLessThanAWeekGiveNoForecast()
    {
        var forecast = FillForecast.From(Scans(600, (0, 500), (2, 540), (5, 580)));

        Assert.Equal(NoForecast.TooShortSpan, forecast.Why);
        Assert.Equal(
            "The scans since 2026-09-01 cover less than a week, which is too short to say when this drive fills.",
            ForecastText.Of(forecast, Date));
    }

    /// <summary>A flat or a falling trend, with every fall below churn, never fills.</summary>
    [Theory]
    [InlineData(500, 500, 500)]
    [InlineData(500, 499.6, 499.2)]
    public void AFlatOrFallingTrendGivesNoForecast(double first, double second, double third)
    {
        var forecast = FillForecast.From(Scans(600, (0, first), (7, second), (14, third)));

        Assert.Equal(NoForecast.NotFilling, forecast.Why);
        Assert.Equal(
            "Used space did not rise over the scans since 2026-09-01, so this drive is not filling.",
            ForecastText.Of(forecast, Date));
    }

    /// <summary>
    /// Three weeks of scans speak for twelve weeks ahead. A drive that fills later than that at the
    /// rate is said not to fill within them, rather than given a date three weeks cannot support.
    /// </summary>
    [Fact]
    public void AFillPastWhatTheScansSpeakForIsNotDated()
    {
        var forecast = FillForecast.From(Scans(2000, (0, 500), (7, 507), (14, 514), (21, 521)));

        Assert.Equal(NoForecast.BeyondHorizon, forecast.Why);
        Assert.Equal(84, forecast.Horizon.TotalDays, precision: 6);
        Assert.Equal(
            "At the rate since 2026-09-01, this drive does not fill within 12 weeks.",
            ForecastText.Of(forecast, Date));
    }

    /// <summary>However long the scans span, nothing is forecast past a year.</summary>
    [Fact]
    public void NoForecastReachesPastAYear()
    {
        var forecast = FillForecast.From(Scans(4000, (0, 500), (100, 600), (200, 700)));

        Assert.Equal(NoForecast.BeyondHorizon, forecast.Why);
        Assert.Equal(FillForecast.MaximumHorizon, forecast.Horizon);
        Assert.EndsWith("does not fill within a year.", ForecastText.Of(forecast, Date));
    }

    /// <summary>
    /// Used space is the volume's own figure, which no route of counting the folders changes, so a set
    /// of scans by both routes forecasts what the same set by one route does, and is not qualified as
    /// approximate the way a comparison of folders across routes is.
    /// </summary>
    [Fact]
    public void AMixedRouteSetForecastsAsOneRouteDoes()
    {
        var points = new (double, double)[] { (0, 500), (7, 507), (14, 514), (21, 521) };
        var oneRoute = Scans(600, points);
        var mixed = oneRoute
            .Select((kept, at) => at % 2 == 0 ? kept with { Strategy = ScanStrategy.ParallelEnumeration } : kept)
            .ToList();

        var forecast = FillForecast.From(mixed);

        Assert.Equal(FillForecast.From(oneRoute), forecast);
        Assert.DoesNotContain("pprox", ForecastText.Of(forecast, Date), StringComparison.Ordinal);
    }

    /// <summary>The words are rounded to the unit that suits how far ahead the fill is.</summary>
    [Theory]
    [InlineData(0.4, "within a day")]
    [InlineData(1.2, "in about 1 day")]
    [InlineData(9.6, "in about 10 days")]
    [InlineData(20, "in about 3 weeks")]
    [InlineData(79, "in about 11 weeks")]
    [InlineData(130, "in about 4 months")]
    public void HowSoonIsRoundedToItsUnit(double days, string expected) =>
        Assert.Equal(expected, ForecastText.Within(TimeSpan.FromDays(days)));

    /// <summary>
    /// Kept summaries of one volume of <paramref name="capacityGigabytes"/>, oldest first: each the
    /// day after <see cref="Start"/> it was taken on and the gigabytes then in use.
    /// </summary>
    private static List<KeptSummary> Scans(long capacityGigabytes, params (double Day, double UsedGigabytes)[] points) =>
    [
        .. points.Select(point => new KeptSummary(
            Volume,
            @"C:\",
            Start.AddDays(point.Day),
            ScanStrategy.MasterFileTable,
            LowerBound: false,
            capacityGigabytes * Gigabyte,
            (long)((capacityGigabytes - point.UsedGigabytes) * Gigabyte))),
    ];
}
