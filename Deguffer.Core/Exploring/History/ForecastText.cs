namespace Deguffer.Core.Exploring.History;

/// <summary>
/// What Explore's What grew card says about when the drive fills (#358): the forecast with the run it
/// was measured over, or why there is none.
///
/// <para>Here rather than in the shell for the reason <see cref="GrowthText"/> is: each sentence states
/// a rule <see cref="FillForecast"/> holds, and a forecast worded more precisely than it was measured
/// is a claim the rule does not make. A date is the shell's to word, in the user's own format.</para>
/// </summary>
public static class ForecastText
{
    /// <summary>What the card says about <paramref name="forecast"/>.</summary>
    /// <param name="date">Words a UTC instant as a date, in the user's own format.</param>
    public static string Of(FillForecast forecast, Func<DateTime, string> date)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(date);

        var since = forecast.Scans > 0 ? date(forecast.SinceUtc) : string.Empty;

        return forecast.Why switch
        {
            NoForecast.None =>
                $"At the rate since {since}, measured over {forecast.Scans} scans, this drive fills {Within(forecast.FillsIn)}.",
            NoForecast.TooFewScans => forecast.Break switch
            {
                ForecastBreak.Fell =>
                    $"Used space fell at the scan of {since}, and too few scans since then show a rate to say when this drive fills.",
                ForecastBreak.Resized =>
                    $"This drive changed size at the scan of {since}, and too few scans since then show a rate to say when it fills.",
                _ =>
                    $"Too few scans of this drive to say when it fills. It takes {FillForecast.MinimumScans}, over a week or more.",
            },
            NoForecast.TooShortSpan =>
                $"The scans since {since} cover less than a week, which is too short to say when this drive fills.",
            NoForecast.NotFilling =>
                $"Used space did not rise over the scans since {since}, so this drive is not filling.",
            NoForecast.BeyondHorizon =>
                $"At the rate since {since}, this drive does not fill within {Span(forecast.Horizon)}.",
            _ => throw new ArgumentOutOfRangeException(nameof(forecast)),
        };
    }

    /// <summary>
    /// How soon, rounded to the unit that suits how far ahead it is: days within a fortnight, weeks
    /// within three months, months beyond.
    /// </summary>
    public static string Within(TimeSpan fillsIn) => fillsIn.TotalDays switch
    {
        < 1 => "within a day",
        < 14 => $"in about {Count(fillsIn.TotalDays, "day")}",
        < 91 => $"in about {Count(fillsIn.TotalDays / 7, "week")}",
        _ => $"in about {Count(fillsIn.TotalDays / (365.25 / 12), "month")}",
    };

    /// <summary>A length of time as the horizon states it: weeks within three months, then months, then a year.</summary>
    private static string Span(TimeSpan span) => span.TotalDays switch
    {
        < 91 => Count(span.TotalDays / 7, "week"),
        < 365 => Count(span.TotalDays / (365.25 / 12), "month"),
        _ => "a year",
    };

    private static string Count(double value, string unit)
    {
        var whole = Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));

        return whole == 1 ? $"1 {unit}" : $"{whole} {unit}s";
    }
}
