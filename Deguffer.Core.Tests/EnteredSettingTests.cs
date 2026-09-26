using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a number typed into a settings box stores. Two of the three decide what gets deleted, and in
/// both of those zero switches a guard off, so the cases that matter are the ones that could land on
/// zero without anybody choosing it: an emptied box, and a fraction of one.
/// </summary>
public sealed class EnteredSettingTests
{
    /// <summary>
    /// An emptied box reports NaN, which survives every comparison in a clamp. Clearing a field is not
    /// a request for anything, so each takes the shipped default rather than its floor.
    /// </summary>
    [Fact]
    public void AnEmptiedBoxStoresTheShippedDefault()
    {
        Assert.Equal(AppPreferences.Default.MinimumTemporaryFileAgeDays, EnteredSetting.TemporaryFileAgeDays(double.NaN));
        Assert.Equal(AppPreferences.Default.FileHistoryRetentionDays, EnteredSetting.FileHistoryRetentionDays(double.NaN));
        Assert.Equal(AppPreferences.Default.KeepFilesChangedWithinHours, EnteredSetting.KeepHours(double.NaN));
    }

    /// <summary>
    /// The shipped seven days is not the floor, which is what makes the fallback above a choice rather
    /// than the clamp doing it.
    /// </summary>
    [Fact]
    public void TheTemporaryFileDefaultIsNotItsFloor()
    {
        Assert.NotEqual(TempDirectoryProvider.MinimumStaleDays, AppPreferences.Default.MinimumTemporaryFileAgeDays);
        Assert.NotEqual(FileHistoryProvider.MinimumRetentionDays, AppPreferences.Default.FileHistoryRetentionDays);
    }

    /// <summary>
    /// §5.3: zero offers everything in a temporary folder however recently it was written. Somebody
    /// typing a fraction of a day is asking for a short window, and the shortest one there is, is a day.
    /// Each value here rounds to zero by at least one rounding mode.
    /// </summary>
    [Theory]
    [InlineData(0.01)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void AFractionOfADayIsADayNotNoAgeLimit(double entered)
    {
        Assert.Equal(1, EnteredSetting.TemporaryFileAgeDays(entered));
    }

    /// <summary>The same for the guard on recently changed files, where zero is off.</summary>
    [Theory]
    [InlineData(0.01)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    public void AFractionOfAnHourIsAnHourNotNoGuard(double entered)
    {
        Assert.Equal(1, EnteredSetting.KeepHours(entered));
    }

    /// <summary>Zero itself is a deliberate choice in both, and passes through.</summary>
    [Fact]
    public void ZeroTypedIsZeroStored()
    {
        Assert.Equal(0, EnteredSetting.TemporaryFileAgeDays(0));
        Assert.Equal(0, EnteredSetting.KeepHours(0));
    }

    /// <summary>
    /// <c>FhManagew.exe -cleanup 0</c> discards every version of everything that has left the
    /// protection scope, so no entry reaches it.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(-30.0)]
    [InlineData(double.NegativeInfinity)]
    public void NoEntryTakesFileHistoryRetentionBelowItsFloor(double entered)
    {
        Assert.Equal(FileHistoryProvider.MinimumRetentionDays, EnteredSetting.FileHistoryRetentionDays(entered));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NegativeInfinity)]
    public void ANegativeEntryIsTheFloor(double entered)
    {
        Assert.Equal(TempDirectoryProvider.MinimumStaleDays, EnteredSetting.TemporaryFileAgeDays(entered));
        Assert.Equal(0, EnteredSetting.KeepHours(entered));
    }

    /// <summary>A number past the box's maximum is the maximum, so the box and the stored value agree.</summary>
    [Theory]
    [InlineData(100_000.0)]
    [InlineData(double.PositiveInfinity)]
    public void AnEntryPastTheMaximumIsTheMaximum(double entered)
    {
        Assert.Equal(TempDirectoryProvider.MaximumStaleDays, EnteredSetting.TemporaryFileAgeDays(entered));
        Assert.Equal(FileHistoryProvider.MaximumRetentionDays, EnteredSetting.FileHistoryRetentionDays(entered));
        Assert.Equal(EnteredSetting.MaximumKeepHours, EnteredSetting.KeepHours(entered));
    }

    /// <summary>
    /// A midpoint rounds away from zero in all three, which is the side that keeps more. The default
    /// rounding sends 2.5 to 2.
    /// </summary>
    [Fact]
    public void AMidpointRoundsToTheSideThatKeepsMore()
    {
        Assert.Equal(3, EnteredSetting.TemporaryFileAgeDays(2.5));
        Assert.Equal(3, EnteredSetting.FileHistoryRetentionDays(2.5));
        Assert.Equal(3, EnteredSetting.KeepHours(2.5));
    }

    [Fact]
    public void AWholeNumberInsideTheBoundsIsStoredAsTyped()
    {
        Assert.Equal(14, EnteredSetting.TemporaryFileAgeDays(14));
        Assert.Equal(30, EnteredSetting.FileHistoryRetentionDays(30));
        Assert.Equal(8, EnteredSetting.KeepHours(8));
    }
}
