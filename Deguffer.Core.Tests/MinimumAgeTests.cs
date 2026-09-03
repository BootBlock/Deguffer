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

        Assert.True(guard.Protects(MinimumAge.NewestFileTimeOf(justNow, longAgo)));
        Assert.True(guard.Protects(MinimumAge.NewestFileTimeOf(longAgo, justNow)));
        Assert.False(guard.Protects(MinimumAge.NewestFileTimeOf(longAgo, longAgo)));
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
    /// The cut-off is the clock the caller gave, less the window they asked for. Nothing subtler,
    /// and pinned because everything else in this type is derived from it.
    ///
    /// <para><b>What this does not prove, stated rather than implied.</b> <c>Within</c> also
    /// normalises a clock that is not already UTC, and dropping that normalisation would move the
    /// cut-off by the host's offset — which protects <em>fewer</em> files, the direction that loses
    /// data. No test here can discriminate on it: the only way to reach the branch is to pass a
    /// local instant, and on a host whose local time is UTC — a build agent, and this project's own
    /// time zone every winter — the conversion is the identity. G8 asks for that to be said outright
    /// rather than covered by a test that passes on a property of the machine.</para>
    /// </summary>
    [Fact]
    public void TakesTheCutOffFromTheClockItIsGiven()
    {
        Assert.Equal(Now.AddHours(-8), MinimumAge.WithinHours(8, Now).KeepFromUtc);
        Assert.Equal(Now - TimeSpan.FromMinutes(90), MinimumAge.Within(TimeSpan.FromMinutes(90), Now).KeepFromUtc);
    }

    [Fact]
    public void RefusesAWindowLongerThanItCanRepresent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinimumAge.Within(MinimumAge.MaximumWindow + TimeSpan.FromDays(1), Now));

        // WithinHours clamps rather than throwing, and it is the entry point the app takes for
        // exactly that reason: the number comes off disk, nothing validates preferences.json on the
        // way in, and a file somebody edited by hand must not stop the app planning. int.MaxValue
        // hours is also past what TimeSpan.FromHours can represent at all, so a caller converting
        // before this point would overflow before reaching the clamp.
        var clamped = MinimumAge.WithinHours(int.MaxValue, Now);

        Assert.True(clamped.IsOn);
        Assert.Equal(Now - MinimumAge.MaximumWindow, clamped.KeepFromUtc);
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
