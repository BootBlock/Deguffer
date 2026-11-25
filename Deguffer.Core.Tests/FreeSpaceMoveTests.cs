using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7's free space before and after a clean, stated from two readings of the volume (issue #858), so the
/// user never has to remember the figure they started from.
/// </summary>
public sealed class FreeSpaceMoveTests
{
    /// <summary>
    /// The change is the difference between the readings and nothing else (§5.4). Here the run is not
    /// consulted at all, so a volume that gained more than any clean removed still says so.
    /// </summary>
    [Fact]
    public void StatesTheReadingBeforeAndHowFarTheVolumeMoved()
    {
        var up = FreeSpaceMove.Between(before: 289_000_000_000, after: 312_000_000_000)!;

        Assert.Equal(23_000_000_000, up.Change);
        Assert.Equal(
            $"Up {FreeSpace.Format(23_000_000_000)} from {FreeSpace.Format(289_000_000_000)} before the clean",
            up.Text);
    }

    /// <summary>Something else writing during the run can leave less free space than before, and the line says so.</summary>
    [Fact]
    public void SaysDownWhenTheVolumeHasLessFreeSpaceThanBefore()
    {
        var down = FreeSpaceMove.Between(before: 1_000_000, after: 900_000)!;

        Assert.Equal(-100_000, down.Change);
        Assert.Equal($"Down {FreeSpace.Format(100_000)} from {FreeSpace.Format(1_000_000)} before the clean", down.Text);
        Assert.Equal(
            $"Unchanged from {FreeSpace.Format(1_000_000)} before the clean",
            FreeSpaceMove.Between(before: 1_000_000, after: 1_000_000)!.Text);
    }

    /// <summary>
    /// Away from the free space it describes, the move says whose space moved, and in every direction,
    /// because nothing beside it does.
    /// </summary>
    [Fact]
    public void TheSentenceNamesTheFreeSpaceInEveryDirection()
    {
        Assert.Equal(
            $"Free space is up {FreeSpace.Format(23_000_000_000)} from {FreeSpace.Format(289_000_000_000)} before the clean.",
            FreeSpaceMove.Between(before: 289_000_000_000, after: 312_000_000_000)!.Sentence);
        Assert.Equal(
            $"Free space is down {FreeSpace.Format(100_000)} from {FreeSpace.Format(1_000_000)} before the clean.",
            FreeSpaceMove.Between(before: 1_000_000, after: 900_000)!.Sentence);
        Assert.Equal(
            $"Free space is unchanged at {FreeSpace.Format(1_000_000)}.",
            FreeSpaceMove.Between(before: 1_000_000, after: 1_000_000)!.Sentence);
    }

    /// <summary>A move from one reading alone would be a guess dressed as a measurement.</summary>
    [Fact]
    public void NeedsBothReadings()
    {
        Assert.Null(FreeSpaceMove.Between(before: null, after: 500));
        Assert.Null(FreeSpaceMove.Between(before: 1_000, after: null));
    }
}
