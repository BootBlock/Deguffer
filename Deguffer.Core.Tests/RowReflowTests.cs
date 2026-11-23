using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A row keeps its name whole: where the name and the chips cannot share a line, the chips move under
/// the name, and the secondary part gives way into whatever is left beside the name.
/// </summary>
public sealed class RowReflowTests
{
    private const double Gap = 8;
    private const double Name = 200;
    private const double Chips = 150;

    [Fact]
    public void ANameAndChipsThatFitShareALine() =>
        Assert.False(RowReflow.Wraps(Name, Chips, Gap, Name + Gap + Chips));

    /// <summary>One pixel short is the point at which a single line would have trimmed the name.</summary>
    [Fact]
    public void ANameAndChipsThatDoNotFitWrap() =>
        Assert.True(RowReflow.Wraps(Name, Chips, Gap, Name + Gap + Chips - 1));

    /// <summary>A row measured with no bound, as a list measures a row for its own width, has room for everything.</summary>
    [Fact]
    public void AnUnboundedRowNeverWraps() =>
        Assert.False(RowReflow.Wraps(Name, Chips, Gap, double.PositiveInfinity));

    /// <summary>A part with no width is not on screen, so no gap stands beside it to tip the row onto two lines.</summary>
    [Fact]
    public void AHiddenPartTakesNoGap() =>
        Assert.False(RowReflow.Wraps(Name, 0, Gap, Name));

    [Fact]
    public void OnOneLineTheSecondaryTextHasTheSpaceBetweenTheNameAndTheChips() =>
        Assert.Equal(100, RowReflow.SlackWidth(wraps: false, Name, Chips, Gap, Name + Gap + 100 + Gap + Chips));

    [Fact]
    public void OnTwoLinesTheSecondaryTextHasTheSpaceBesideTheName() =>
        Assert.Equal(300 - Name - Gap, RowReflow.SlackWidth(wraps: true, Name, Chips, Gap, 300));

    [Fact]
    public void TheSecondaryTextNeverHasLessThanNothing() =>
        Assert.Equal(0, RowReflow.SlackWidth(wraps: true, Name, Chips, Gap, Name - 20));

    [Fact]
    public void AnUnboundedRowGivesTheSecondaryTextAllItWants() =>
        Assert.Equal(double.PositiveInfinity, RowReflow.SlackWidth(wraps: false, Name, Chips, Gap, double.PositiveInfinity));
}
