namespace Deguffer.Core.Exploring.History;

/// <summary>Why a volume's kept scans give no forecast of when it fills, or that they give one.</summary>
public enum NoForecast
{
    /// <summary>They give one.</summary>
    None,

    /// <summary>Fewer than <see cref="FillForecast.MinimumScans"/> scans are in the run the rate is taken over.</summary>
    TooFewScans,

    /// <summary>The run the rate is taken over spans less than <see cref="FillForecast.MinimumSpan"/>.</summary>
    TooShortSpan,

    /// <summary>Used space did not rise over the run, so at that rate the volume never fills.</summary>
    NotFilling,

    /// <summary>
    /// At the rate, the volume fills later than the run can speak for: see
    /// <see cref="FillForecast.Horizon"/>.
    /// </summary>
    BeyondHorizon,
}

/// <summary>What ended the run of scans before the one a rate is taken over.</summary>
public enum ForecastBreak
{
    /// <summary>Nothing: the run starts at the oldest kept scan.</summary>
    None,

    /// <summary>
    /// Used space fell by more than <see cref="FillForecast.FallOf"/>: a clean, a removal from Explore
    /// or an uninstall.
    /// </summary>
    Fell,

    /// <summary>The volume's capacity changed, so it was resized or is another size of disk.</summary>
    Resized,
}

/// <summary>
/// When a volume fills at the rate its kept scans rose at (#358), or why that cannot be said.
///
/// <para><b>Over the run since the last fall, never across one.</b> A clean, a removal or an uninstall
/// is a step down, and a straight line fitted across it is flatter than the rate either side of it, so
/// the forecast would be too late by however much was removed. The run starts at the newest scan whose
/// used space fell from the one before, or whose capacity differs from it, and the line is fitted by
/// least squares over the scans from there, so one scan's churn moves it less than a line through two
/// ends would.</para>
///
/// <para><b>A forecast is no more precise than what it was measured from.</b> It needs
/// <see cref="MinimumScans"/> scans over <see cref="MinimumSpan"/> at least, and reaches no further
/// ahead than <see cref="Horizon"/>. The words round it to days, weeks or months by how far ahead it
/// is (<see cref="ForecastText"/>).</para>
///
/// <para><b>The route a scan took does not qualify it.</b> Used space is what the volume said was in
/// use, its capacity less its free space, and Windows states both whichever route counted the folders.
/// A walk counting less than the file table changes what a folder seems to hold, which is why a
/// comparison across routes is approximate (<see cref="GrowthApproximation.RouteChanged"/>). It does
/// not change what the volume says is used.</para>
/// </summary>
/// <param name="Why">Why there is no forecast, or <see cref="NoForecast.None"/>.</param>
/// <param name="SinceUtc">When the run the rate is taken over starts, or default where no scan is kept.</param>
/// <param name="Scans">How many scans are in that run.</param>
/// <param name="Break">What ended the run before it.</param>
/// <param name="BytesPerDay">The fitted rate, or zero where it was not fitted.</param>
/// <param name="FillsIn">
/// How long after the newest scan the volume fills at that rate, where <paramref name="Why"/> is
/// <see cref="NoForecast.None"/>, or zero.
/// </param>
/// <param name="Horizon">How far ahead the run can speak for, or zero where it was not fitted.</param>
public sealed record FillForecast(
    NoForecast Why,
    DateTime SinceUtc,
    int Scans,
    ForecastBreak Break,
    double BytesPerDay,
    TimeSpan FillsIn,
    TimeSpan Horizon)
{
    /// <summary>The fewest scans a rate is fitted to. Two make a line whatever the scans say, and three test it.</summary>
    public const int MinimumScans = 3;

    /// <summary>
    /// How far ahead a run can speak for, as a multiple of the time it spans. Four weeks of scans say
    /// little about the next year, and a forecast past this is stated as not filling within it.
    /// </summary>
    public const int HorizonPerSpan = 4;

    private const long Gibibyte = 1L << 30;

    /// <summary>The shortest time a rate is fitted over. A day's scans measure a download, not a trend.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromDays(7);

    /// <summary>The furthest ahead any run speaks for, however long it spans.</summary>
    public static readonly TimeSpan MaximumHorizon = TimeSpan.FromDays(365);

    /// <summary>
    /// The fall in used space between two scans that ends a run: a gibibyte, or a hundredth of a
    /// volume too small for that. Less is churn, the temporary files and updates that come and go
    /// between any two scans, and the fit absorbs it.
    /// </summary>
    public static long FallOf(long capacity) => Math.Min(Gibibyte, Math.Max(1, capacity / 100));

    /// <summary>
    /// The forecast <paramref name="kept"/> gives, a volume's kept summaries oldest first, as
    /// <see cref="ScanHistory.Kept"/> lists them.
    /// </summary>
    public static FillForecast From(IReadOnlyList<KeptSummary> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        if (kept.Count == 0)
        {
            return new FillForecast(NoForecast.TooFewScans, default, 0, ForecastBreak.None, 0, TimeSpan.Zero, TimeSpan.Zero);
        }

        var (start, broken) = RunStart(kept);
        var run = kept.Count - start;
        var since = kept[start].TakenUtc;
        var newest = kept[^1];
        var span = newest.TakenUtc - since;

        if (run < MinimumScans)
        {
            return new FillForecast(NoForecast.TooFewScans, since, run, broken, 0, TimeSpan.Zero, TimeSpan.Zero);
        }

        if (span < MinimumSpan)
        {
            return new FillForecast(NoForecast.TooShortSpan, since, run, broken, 0, TimeSpan.Zero, TimeSpan.Zero);
        }

        var rate = Slope(kept, start);
        var horizon = TimeSpan.FromTicks(Math.Min(MaximumHorizon.Ticks, span.Ticks * HorizonPerSpan));

        if (rate <= 0)
        {
            return new FillForecast(NoForecast.NotFilling, since, run, broken, rate, TimeSpan.Zero, horizon);
        }

        var days = newest.FreeBytes / rate;

        return days > horizon.TotalDays
            ? new FillForecast(NoForecast.BeyondHorizon, since, run, broken, rate, TimeSpan.Zero, horizon)
            : new FillForecast(NoForecast.None, since, run, broken, rate, TimeSpan.FromDays(days), horizon);
    }

    /// <summary>Where the run the rate is taken over starts in <paramref name="kept"/>, and what ended the one before.</summary>
    private static (int Start, ForecastBreak Break) RunStart(IReadOnlyList<KeptSummary> kept)
    {
        for (var at = kept.Count - 1; at > 0; at--)
        {
            KeptSummary before = kept[at - 1], after = kept[at];

            if (before.TotalBytes != after.TotalBytes)
            {
                return (at, ForecastBreak.Resized);
            }

            if (before.UsedBytes - after.UsedBytes > FallOf(after.TotalBytes))
            {
                return (at, ForecastBreak.Fell);
            }
        }

        return (0, ForecastBreak.None);
    }

    /// <summary>The least-squares slope of used space against time over <paramref name="kept"/> from <paramref name="start"/>, in bytes a day.</summary>
    private static double Slope(IReadOnlyList<KeptSummary> kept, int start)
    {
        var origin = kept[start].TakenUtc;
        var count = kept.Count - start;
        double meanDays = 0, meanBytes = 0;

        for (var at = start; at < kept.Count; at++)
        {
            meanDays += (kept[at].TakenUtc - origin).TotalDays / count;
            meanBytes += (double)kept[at].UsedBytes / count;
        }

        double across = 0, spread = 0;

        for (var at = start; at < kept.Count; at++)
        {
            var days = (kept[at].TakenUtc - origin).TotalDays - meanDays;
            across += days * (kept[at].UsedBytes - meanBytes);
            spread += days * days;
        }

        // The run spans MinimumSpan at least, so its times are never all one.
        return across / spread;
    }
}
