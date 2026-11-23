using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>A row of controls breaks a line only where the next control would cross the edge.</summary>
public sealed class FlowTests
{
    private const double Gap = 8;

    [Fact]
    public void AControlThatFitsStaysOnTheLine() =>
        Assert.False(Flow.Breaks(line: 100, width: 50, Gap, available: 158));

    /// <summary>One pixel over is where the control would have been drawn half off the edge.</summary>
    [Fact]
    public void AControlThatWouldCrossTheEdgeStartsANewLine() =>
        Assert.True(Flow.Breaks(line: 100, width: 50, Gap, available: 157));

    /// <summary>A control wider than the row would be no better off on the next line, and a line with nothing on it would be a gap.</summary>
    [Fact]
    public void TheFirstControlOnALineNeverBreaks() =>
        Assert.False(Flow.Breaks(line: 0, width: 500, Gap, available: 100));

    /// <summary>A collapsed control has no width, and must not push a line break with a gap it does not draw.</summary>
    [Fact]
    public void AHiddenControlNeverBreaks() =>
        Assert.False(Flow.Breaks(line: 100, width: 0, Gap, available: 100));
}
