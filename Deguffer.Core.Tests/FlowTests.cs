using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>A row of controls breaks a line only where the next control would cross the edge.</summary>
public sealed class FlowTests
{
    private const double Gap = 8;

    [Fact]
    public void AControlThatFitsStaysOnTheLine() =>
        Assert.False(Flow.Breaks(line: 100, width: 50, Gap, available: 158));

    /// <summary>Past the rounding slack, the control would be drawn visibly off the edge.</summary>
    [Fact]
    public void AControlThatWouldCrossTheEdgeStartsANewLine() =>
        Assert.True(Flow.Breaks(line: 100, width: 50, Gap, available: 156.9));

    /// <summary>
    /// A row arranged at the width it asked for can get a fraction of a pixel less back from layout
    /// rounding at 125% or 150%, and its last control must stay on the line it was measured on.
    /// </summary>
    [Fact]
    public void ARowShortByARoundingFractionKeepsItsLine() =>
        Assert.False(Flow.Breaks(line: 100, width: 50, Gap, available: 157.4));

    /// <summary>A control wider than the row would be no better off on the next line, and a line with nothing on it would be a gap.</summary>
    [Fact]
    public void TheFirstControlOnALineNeverBreaks() =>
        Assert.False(Flow.Breaks(line: 0, width: 500, Gap, available: 100));

    /// <summary>A collapsed control has no width, and must not push a line break with a gap it does not draw.</summary>
    [Fact]
    public void AHiddenControlNeverBreaks() =>
        Assert.False(Flow.Breaks(line: 100, width: 0, Gap, available: 100));
}
