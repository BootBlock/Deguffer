using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The guard on recently touched files, as a rule rather than as a deletion.
///
/// Everything that acts on it — the two scanners, the two removers, the provider base — asks this
/// type and nothing else, so the rule is checked once here and the callers are checked for asking.
/// </summary>
public class MinimumAgeTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Off is the shipped default, and <c>default</c> has to mean it. Every seam that takes a guard
    /// defaults its parameter, so a caller that says nothing must measure and delete exactly what it
    /// did before the setting existed.
    /// </summary>
    [Fact]
    public void OffProtectsNothingHoweverRecent()
    {
        var written = Now.ToFileTimeUtc();

        Assert.False(MinimumAge.Off.IsOn);
        Assert.False(MinimumAge.Off.Protects(written));
        Assert.Equal(MinimumAge.Off, default);
    }

    [Fact]
    public void AWindowOfZeroOrLessIsOff()
    {
        Assert.False(MinimumAge.Within(TimeSpan.Zero, Now).IsOn);
        Assert.False(MinimumAge.Within(TimeSpan.FromHours(-3), Now).IsOn);
        Assert.False(MinimumAge.WithinHours(0, Now).IsOn);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    [InlineData(400, false)]
    public void KeepsWhatWasWrittenInsideTheWindowAndNothingOlder(int hoursAgo, bool kept)
    {
        var guard = MinimumAge.WithinHours(8, Now);

        Assert.Equal(kept, guard.Protects(Now.AddHours(-hoursAgo).ToFileTimeUtc()));
    }

    /// <summary>
    /// A file copied or extracted into a cache keeps the source's last-write time, which can be
    /// years old, while its creation time here is now. Taking last-write alone would delete exactly
    /// the file the setting exists to keep.
    /// </summary>
    [Fact]
    public void TakesTheNewerOfCreationAndLastWrite()
    {
        var guard = MinimumAge.WithinHours(8, Now);

        var longAgo = Now.AddYears(-3).ToFileTimeUtc();
        var justNow = Now.AddMinutes(-5).ToFileTimeUtc();

        Assert.True(guard.Protects(createdFileTime: justNow, lastWrittenFileTime: longAgo));
        Assert.True(guard.Protects(createdFileTime: longAgo, lastWrittenFileTime: justNow));
        Assert.False(guard.Protects(createdFileTime: longAgo, lastWrittenFileTime: longAgo));
    }

    /// <summary>
    /// NTFS writes zero for a timestamp it never set, and a record the reader could not date arrives
    /// here the same way. Zero is the start of 1601, so it is older than any window and the file is
    /// deletable — which is the direction that keeps "we could not tell" out of the protected set.
    /// </summary>
    [Fact]
    public void ATimestampNtfsNeverSetIsNotRecent()
    {
        Assert.False(MinimumAge.WithinHours(8, Now).Protects(0));
    }

    /// <summary>
    /// The cut-off is an instant taken once, so a guard made an hour ago protects the same files an
    /// hour later. That is what lets a preview promise what the clean will do, however long the
    /// preview sits on screen first.
    /// </summary>
    [Fact]
    public void TheCutOffDoesNotMoveWithTheClock()
    {
        var guard = MinimumAge.WithinHours(8, Now);
        var file = Now.AddHours(-7).ToFileTimeUtc();

        Assert.True(guard.Protects(file));

        // Two hours later the same file is nine hours old, and the same guard still keeps it.
        var later = MinimumAge.WithinHours(8, Now.AddHours(2));

        Assert.True(guard.Protects(file));
        Assert.False(later.Protects(file));
    }

    /// <summary>
    /// The clock is an argument, and nothing says it arrives in UTC. A local instant has to be
    /// converted before it is compared: a <see cref="DateTime"/> compares on raw ticks whatever its
    /// kind claims, so normalising afterwards would set the cut-off an offset's worth away from the
    /// one the caller asked for — an hour of files kept or deleted by mistake, depending on the
    /// sign.
    /// </summary>
    [Fact]
    public void ReadsTheClockItIsGivenAsTheInstantItStandsFor()
    {
        Assert.Equal(
            MinimumAge.WithinHours(8, Now).KeepFromUtc,
            MinimumAge.WithinHours(8, Now.ToLocalTime()).KeepFromUtc);
    }

    [Fact]
    public void RefusesAWindowLongerThanItCanRepresent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinimumAge.Within(MinimumAge.MaximumWindow + TimeSpan.FromDays(1), Now));

        // The hours entry point clamps rather than throwing: it is fed by a stored preference, and
        // a settings file somebody edited by hand must not stop the app planning.
        Assert.True(MinimumAge.WithinHours(int.MaxValue, Now).IsOn);
    }

    [Theory]
    [InlineData(1, "hour")]
    [InlineData(8, "8 hours")]
    [InlineData(47, "47 hours")]
    [InlineData(48, "2 days")]
    [InlineData(60, "60 hours")]
    [InlineData(168, "7 days")]
    public void DescribesTheWindowTheUserChose(int hours, string expected)
    {
        Assert.Equal(expected, MinimumAge.WithinHours(hours, Now).Describe());
    }
}
