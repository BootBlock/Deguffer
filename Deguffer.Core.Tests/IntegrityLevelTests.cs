using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1 refuses a process above Deguffer's own integrity level, because Windows may drop a posted
/// message to one without saying so. The comparison is by value: a level between two named ones, such
/// as the medium plus an increment Windows hands a UIAccess application, is above medium, and a
/// comparison that grouped both into one band would let it through.
/// </summary>
public sealed class IntegrityLevelTests
{
    private const int Untrusted = 0x0;
    private const int Low = 0x1000;
    private const int Medium = 0x2000;
    private const int MediumUiAccess = 0x2010;
    private const int MediumHigh = 0x2100;
    private const int High = 0x3000;
    private const int System = 0x4000;

    [Theory]
    [InlineData(High, Medium)]
    [InlineData(System, High)]
    [InlineData(MediumUiAccess, Medium)]
    [InlineData(MediumHigh, Medium)]
    [InlineData(Low, Untrusted)]
    public void ALevelAboveDegufferIsRefused(int target, int own) =>
        Assert.Equal(Answer.Yes, IntegrityLevel.Above(target, own));

    [Theory]
    [InlineData(Medium, Medium)]
    [InlineData(Low, Medium)]
    [InlineData(Untrusted, Medium)]
    [InlineData(Medium, MediumUiAccess)]
    [InlineData(High, System)]
    public void ALevelAtOrBelowDegufferIsNot(int target, int own) =>
        Assert.Equal(Answer.No, IntegrityLevel.Above(target, own));

    /// <summary>
    /// A level Windows would not read is not a level Deguffer can post below, so it is neither above
    /// nor not above.
    /// </summary>
    [Fact]
    public void ALevelThatWillNotBeReadIsUnreadable()
    {
        Assert.Equal(Answer.Unreadable, IntegrityLevel.Above(null, Medium));
        Assert.Equal(Answer.Unreadable, IntegrityLevel.Above(High, null));
    }
}
