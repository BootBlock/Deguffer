using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>Parts sit side by side only where each column keeps the width it was laid out for.</summary>
public sealed class ColumnsTests
{
    [Fact]
    public void ColumnsThatEachGetTheirMinimumSitSideBySide() =>
        Assert.True(Columns.SideBySide(2, minimum: 360, gap: 32, available: 752));

    /// <summary>One pixel short is where a column's content would start running off its edge.</summary>
    [Fact]
    public void ColumnsThatWouldBeSqueezedStack() =>
        Assert.False(Columns.SideBySide(2, minimum: 360, gap: 32, available: 751));

    [Fact]
    public void OnePartIsAlwaysItsOwnColumn() =>
        Assert.True(Columns.SideBySide(1, minimum: 360, gap: 32, available: 100));

    [Fact]
    public void EqualColumnsShareWhatTheGapsLeave() =>
        Assert.Equal(400, Columns.Width(2, gap: 32, available: 832));
}
