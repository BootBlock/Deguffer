using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Tests;

/// <summary>
/// How what Windows lists about System Protection is read: which shadow copies can be ruled out as a
/// restore point's, and the timestamps WMI gives restore points.
/// </summary>
public sealed class SystemProtectionReadingTests
{
    private static readonly Guid OtherProvider = new("11111111-2222-3333-4444-555555555555");

    private static ShadowCopy Copy(Guid provider, int attributes) =>
        new(Guid.NewGuid(), provider, attributes, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", DateTime.Now);

    /// <summary>
    /// Only the system provider makes a client-accessible copy with its writers involved, which is the
    /// kind System Restore keeps. Any other copy is never a restore point's, and must survive.
    /// </summary>
    [Theory]
    [InlineData(0x1 | 0x4 | 0x8, true)] // VSS_CTX_CLIENT_ACCESSIBLE_WRITERS
    [InlineData(0x1 | 0x4 | 0x8 | 0x10, false)] // VSS_CTX_CLIENT_ACCESSIBLE: made without writers
    [InlineData(0x1 | 0x8, false)] // VSS_CTX_APP_ROLLBACK: a backup product's
    [InlineData(0x1 | 0x8 | 0x10, false)] // VSS_CTX_NAS_ROLLBACK
    [InlineData(0x0, false)] // VSS_CTX_BACKUP: released when the backup ends
    public void OnlyAClientAccessibleCopyWithWritersFromTheSystemProviderCouldBeARestorePoints(int attributes, bool could) =>
        Assert.Equal(could, Copy(ShadowCopy.SystemProvider, attributes).CouldBeRestorePoint);

    [Fact]
    public void ACopyFromAnotherProviderIsNeverARestorePoints() =>
        Assert.False(Copy(OtherProvider, 0x1 | 0x4 | 0x8).CouldBeRestorePoint);

    [Fact]
    public void ACimTimeReadsWithItsOffsetFromUtc()
    {
        var read = RestorePointCalls.CimTime("20261003101530.123456+060");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 10, 15, 30, 123, TimeSpan.FromHours(1)).AddTicks(4560).LocalDateTime, read);
    }

    [Fact]
    public void ACimTimeBehindUtcReadsAsSuch()
    {
        var read = RestorePointCalls.CimTime("20261003101530.000000-300");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 10, 15, 30, TimeSpan.FromHours(-5)).LocalDateTime, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("20261003101530")]
    [InlineData("20261003101530.000000*000")]
    [InlineData("2026100310153X.000000+000")]
    [InlineData("20261003101530.000000+0000")]
    [InlineData("20261003101530.000000+900")]
    public void AnythingElseIsNotATime(string text) => Assert.Null(RestorePointCalls.CimTime(text));
}
