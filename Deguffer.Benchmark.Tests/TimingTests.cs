namespace Deguffer.Benchmark.Tests;

/// <summary>
/// What a run keeps is measured with it still referenced, and let go once measured, so one run's
/// result is neither missed nor carried into the next run's figures.
///
/// <para>Run alone. The heap is the whole process's, so a test running beside these would add what
/// it holds to the figure measured here.</para>
/// </summary>
[Collection(nameof(TimingTests))]
public sealed class TimingTests
{
    private const int Held = 32 << 20;

    [Fact]
    public void MeasuresWhatTheResultKeepsAndLetsItGo()
    {
        var sample = Timing.Measure(_ => new RunTally(1, 0, Complete: true, Kept: new byte[Held]), CancellationToken.None);

        Assert.InRange(sample.KeptBytes, Held, Held + (8 << 20));
        Assert.Null(sample.Tally.Kept);
    }

    [Fact]
    public void ARunThatKeepsNothingKeepsNothing() =>
        Assert.Equal(0, Timing.Measure(_ => new RunTally(1, 0, Complete: true), CancellationToken.None).KeptBytes);
}

/// <summary>Keeps <see cref="TimingTests"/> from running beside any other test.</summary>
[CollectionDefinition(nameof(TimingTests), DisableParallelization = true)]
public sealed class TimingTestsRunAlone
{
}
