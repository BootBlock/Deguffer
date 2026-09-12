using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the rest of Deguffer sees of a process table: the undocumented figures where the check passed,
/// and nothing at all where it did not. Zero in place of an unchecked figure would read as a process
/// holding nothing (§7.2), so a figure that is off has to be unmistakably absent.
/// </summary>
public sealed class ProcessMemoryTableTests
{
    private static readonly ParsedProcessTable Parsed = new(
        [new ProcessRecord(1_204, 4, "alpha.exe", CommitCharge: 31_000_000, PrivateWorkingSet: 22_000_000, CreationTime: 133_900_000_000_000_001)],
        Complete: true);

    [Fact]
    public void CheckedFiguresAreCarriedThrough()
    {
        var table = ProcessMemoryTable.From(Parsed, ProcessFigures.Checked);

        Assert.Equal(
            new ProcessMemory(1_204, 4, "alpha.exe", 31_000_000, PrivateWorkingSet: 22_000_000, CreationTime: 133_900_000_000_000_001),
            Assert.Single(table.Processes));
    }

    [Theory]
    [InlineData(ProcessFigures.NothingToCheckAgainst)]
    [InlineData(ProcessFigures.OwnProcessNotListed)]
    [InlineData(ProcessFigures.CreationTimeDisagrees)]
    [InlineData(ProcessFigures.PrivateWorkingSetDisagrees)]
    [InlineData(ProcessFigures.NotOnThisArchitecture)]
    public void FiguresThatFailedTheCheckAreAbsentRatherThanZero(ProcessFigures figures)
    {
        var table = ProcessMemoryTable.From(Parsed, figures);

        Assert.Equal(figures, table.Figures);
        Assert.Equal(
            new ProcessMemory(1_204, 4, "alpha.exe", 31_000_000, PrivateWorkingSet: null, CreationTime: null),
            Assert.Single(table.Processes));
    }

    /// <summary>Every value the check can give, so a verdict added later cannot slip past the theory above.</summary>
    [Fact]
    public void EveryVerdictButCheckedIsCoveredAbove() =>
        Assert.Equal(6, Enum.GetValues<ProcessFigures>().Length);

    [Fact]
    public void AnIncompleteWalkStaysIncomplete() =>
        Assert.False(ProcessMemoryTable.From(Parsed with { Complete = false }, ProcessFigures.Checked).Complete);
}
