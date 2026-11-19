using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// When work done on several threads next says how far it has got: once an interval has passed since
/// the last report, or since the work began, and to one thread only.
/// </summary>
public sealed class ReportCadenceTests
{
    private static readonly TimeSpan Almost = ReportCadence.Interval - TimeSpan.FromMilliseconds(1);

    private readonly ManualTimeProvider _clock = new();

    [Fact]
    public void NothingIsDueBeforeTheFirstInterval()
    {
        var cadence = new ReportCadence(_clock);

        Assert.False(cadence.Claim());
        _clock.Advance(Almost);
        Assert.False(cadence.Claim());
    }

    [Fact]
    public void AReportIsDueOncePerIntervalAsTheClockMoves()
    {
        var cadence = new ReportCadence(_clock);
        List<bool> claims = [];

        _clock.Advance(Almost);
        claims.Add(cadence.Claim());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        claims.Add(cadence.Claim());
        claims.Add(cadence.Claim());
        _clock.Advance(Almost);
        claims.Add(cadence.Claim());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        claims.Add(cadence.Claim());

        Assert.Equal([false, true, false, false, true], claims);
    }

    /// <summary>The work runs on several threads, and only one of those that find a report due makes it.</summary>
    [Fact]
    public void ThreadsThatFindAReportDueTogetherClaimItOnce()
    {
        var cadence = new ReportCadence(_clock);
        var claimed = 0;

        _clock.Advance(ReportCadence.Interval);
        Parallel.For(0, 100_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            if (cadence.Claim())
            {
                Interlocked.Increment(ref claimed);
            }
        });

        Assert.Equal(1, claimed);
    }
}
