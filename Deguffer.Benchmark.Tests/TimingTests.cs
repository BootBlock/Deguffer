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

    // What else the heap figure moves by between two collections a moment apart: a few hundred
    // bytes in these tests' runs, with room to spare and far below any size asserted here.
    private const int Noise = 64 << 10;

    // What one run leaves behind, as a shared pool would hold it.
    private static byte[]? _leftBehind;

    [Fact]
    public void MeasuresWhatTheResultKeepsAndLetsItGo()
    {
        var sample = Timing.Measure(_ => new RunTally(1, 0, Complete: true, Kept: new KeptResult(new byte[Held])), CancellationToken.None);

        Assert.InRange(sample.KeptBytes, Held - Noise, Held + Noise);
        Assert.Null(sample.Tally.Kept);
    }

    /// <summary>
    /// A run leaves buffers in shared pools for the next one to rent, which outlive it but are not
    /// what its result keeps. Here the run keeps such a buffer in a field of its own.
    /// </summary>
    [Fact]
    public void WhatTheRunLeavesBehindIsNotCountedAsKept()
    {
        try
        {
            var sample = Timing.Measure(
                _ =>
                {
                    _leftBehind = new byte[Held];
                    return new RunTally(1, 0, Complete: true, Kept: new KeptResult(new byte[Held / 4]));
                },
                CancellationToken.None);

            Assert.InRange(sample.KeptBytes, (Held / 4) - Noise, (Held / 4) + Noise);
        }
        finally
        {
            _leftBehind = null;
        }
    }

    /// <summary>
    /// The processor time is what the run spent and nothing from before it, so work done before the
    /// clock starts cannot make a run look busier than it was.
    /// </summary>
    [Fact]
    public void MeasuresTheProcessorTimeOfTheRunAlone()
    {
        Spin(TimeSpan.FromMilliseconds(400));

        var idle = Timing.Measure(_ => new RunTally(1, 0, Complete: true), CancellationToken.None);
        var busy = Timing.Measure(
            _ =>
            {
                Spin(TimeSpan.FromMilliseconds(200));
                return new RunTally(1, 0, Complete: true);
            },
            CancellationToken.None);

        Assert.InRange(idle.ProcessorTime, TimeSpan.Zero, TimeSpan.FromMilliseconds(150));
        Assert.True(busy.ProcessorTime >= TimeSpan.FromMilliseconds(200), $"{busy.ProcessorTime} of processor time");
    }

    [Fact]
    public void ARunThatKeepsNothingKeepsNothing() =>
        Assert.Equal(0, Timing.Measure(_ => new RunTally(1, 0, Complete: true), CancellationToken.None).KeptBytes);

    /// <summary>
    /// Keeps the processor busy until the process has spent <paramref name="processor"/> of it, which on
    /// a shared machine can take longer than that by the clock, but never less.
    /// </summary>
    private static void Spin(TimeSpan processor)
    {
        var until = Environment.CpuUsage.TotalTime + processor;
        while (Environment.CpuUsage.TotalTime < until)
        {
            Thread.SpinWait(1000);
        }
    }
}

/// <summary>Keeps <see cref="TimingTests"/> from running beside any other test.</summary>
[CollectionDefinition(nameof(TimingTests), DisableParallelization = true)]
public sealed class TimingTestsRunAlone
{
}
