using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// The four bytes a date is kept in, and what they give up.
///
/// <para>Worth its own tests because it is the one place the two scan routes are made to agree. The
/// file table reports 100-nanosecond ticks and the walk reports a <see cref="DateTime"/>, and if
/// those two do not land on the same value for the same instant then the same disk is dated two
/// different ways depending on how the user happened to start the app.</para>
/// </summary>
public class ExploreTimestampTests
{
    private static readonly DateTime Instant = new(2024, 3, 11, 14, 32, 45, DateTimeKind.Utc);

    /// <summary>
    /// The whole point of the type: the same instant, arriving by either route, is one value.
    ///
    /// <para>Asserted on the packed number rather than on what it reads back as. Two conversions
    /// that agreed on a date while disagreeing on the number would still sort, compare and roll up
    /// differently.</para>
    /// </summary>
    [Fact]
    public void BothRoutesReachTheSameValueForTheSameInstant()
    {
        var fromWalk = ExploreTimestamp.FromUtc(Instant);
        var fromTable = ExploreTimestamp.FromFileTime(Instant.ToFileTimeUtc());

        Assert.Equal(fromWalk, fromTable);
        Assert.Equal(fromWalk.MinutesSinceWindowsEpoch, fromTable.MinutesSinceWindowsEpoch);
    }

    /// <summary>
    /// Seconds are what is given up, and they are given up downwards. Rounding to the nearest
    /// minute would move a date into the following one, which is visible on any file written in the
    /// last thirty seconds of a day.
    /// </summary>
    [Fact]
    public void KeepsTheMinuteAndDropsTheSecondsBeneathIt()
    {
        Assert.Equal(
            new DateTime(2024, 3, 11, 14, 32, 0, DateTimeKind.Utc),
            ExploreTimestamp.FromUtc(Instant).Utc);

        // The last second of the minute is still that minute, not the next one.
        Assert.Equal(
            ExploreTimestamp.FromUtc(Instant).Utc,
            ExploreTimestamp.FromUtc(new DateTime(2024, 3, 11, 14, 32, 59, DateTimeKind.Utc)).Utc);
    }

    /// <summary>
    /// A local time must be converted rather than reinterpreted. Reading one as though it were
    /// already UTC produces an error that shows up as a few hours' drift and never as a failure.
    ///
    /// <para><b>The instant is searched for, not fixed.</b> An offset is what makes this test able
    /// to fail at all, and a zone that keeps summer time has one for only half the year — so a fixed
    /// March date passed identically with the conversion deleted, on a machine in London. This walks
    /// the year and takes the first month the machine's own zone is actually offset at.</para>
    ///
    /// <para>A machine set to UTC has no such month, and on one nothing here can tell a converted
    /// local time from an unconverted one. That is stated rather than hidden: the test asserts the
    /// offset it found, so a vacuous run is visible in the assertion rather than silent in a pass.
    /// </para>
    /// </summary>
    [Fact]
    public void ConvertsALocalTimeRatherThanAssumingItIsAlreadyUniversal()
    {
        var offsetAt = OffsetInstant();
        var local = (offsetAt ?? Instant).ToLocalTime();

        Assert.Equal(DateTimeKind.Local, local.Kind);
        Assert.Equal(ExploreTimestamp.FromUtc(offsetAt ?? Instant), ExploreTimestamp.FromUtc(local));

        // What was actually proved. On a zone with an offset the assertion above discriminates; on
        // UTC it cannot, and this says which run happened rather than leaving the two
        // indistinguishable.
        Assert.Equal(
            offsetAt is not null,
            local.Ticks != (offsetAt ?? Instant).Ticks);
    }

    /// <summary>
    /// The first month of a year at which this machine's zone is offset from UTC, or null on a
    /// machine that is set to UTC all year.
    /// </summary>
    private static DateTime? OffsetInstant()
    {
        for (var month = 0; month < 12; month++)
        {
            var candidate = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc).AddMonths(month);

            if (TimeZoneInfo.Local.GetUtcOffset(candidate) != TimeSpan.Zero)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The value NTFS writes for a time it never set, and what .NET answers for an entry that is no
    /// longer there. It must read as "we could not tell" and never as a date, because a date is
    /// what invites somebody to act on something — and January 1601 is the oldest such invitation
    /// there is.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void AnAbsentOrImpossibleFileTimeIsNotADate(long fileTime)
    {
        var timestamp = ExploreTimestamp.FromFileTime(fileTime);

        Assert.False(timestamp.IsKnown);
        Assert.Null(timestamp.Utc);
        Assert.Equal(ExploreTimestamp.Unknown, timestamp);
    }

    /// <summary>The start of the Windows epoch itself is that same absent value, arriving as a date.</summary>
    [Fact]
    public void TheStartOfTheWindowsEpochIsNotADateEither()
    {
        var epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(ExploreTimestamp.FromUtc(epoch).IsKnown);
    }

    /// <summary>
    /// A date past what four bytes reach is unknown rather than wrapped. Narrowing it silently
    /// would put a file in 5684 somewhere in the last century, and a plausible wrong date is worse
    /// than none.
    /// </summary>
    [Fact]
    public void ADateBeyondTheRangeIsUnknownRatherThanWrapped()
    {
        var pastTheRange = ((long)int.MaxValue + 1) * TimeSpan.TicksPerMinute;

        Assert.False(ExploreTimestamp.FromFileTime(pastTheRange).IsKnown);
        Assert.True(ExploreTimestamp.FromFileTime(pastTheRange - TimeSpan.TicksPerMinute).IsKnown);
    }

    /// <summary>
    /// What the subtree roll-up is built on. Unknown has to lose to every real date: a directory
    /// holding one file that could be dated and one that could not is dated by the one that could,
    /// not reduced to unknown by the other.
    /// </summary>
    [Fact]
    public void TheNewerOfTwoPrefersARealDateToAnAbsentOne()
    {
        var older = ExploreTimestamp.FromUtc(Instant);
        var newer = ExploreTimestamp.FromUtc(Instant.AddDays(1));

        Assert.Equal(newer, ExploreTimestamp.Newer(older, newer));
        Assert.Equal(newer, ExploreTimestamp.Newer(newer, older));
        Assert.Equal(older, ExploreTimestamp.Newer(older, ExploreTimestamp.Unknown));
        Assert.Equal(older, ExploreTimestamp.Newer(ExploreTimestamp.Unknown, older));
    }
}
