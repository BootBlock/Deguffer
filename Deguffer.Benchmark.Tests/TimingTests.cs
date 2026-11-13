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

    // How far a kept figure may stray from what the run kept. Running alone does not make the heap
    // quiet: the runner and the work earlier tests left behind allocate on other threads between the
    // two readings, by an amount nothing bounds, seen at up to 76 KB on a loaded machine. The bound is
    // set by what the assertions must tell apart instead: the result from nothing, and from what the
    // run leaves behind, which differ by at least Held / 4. A megabyte is an eighth of that.
    private const int Tolerance = 1 << 20;

    // What one run leaves behind, as a shared pool would hold it.
    private static byte[]? _leftBehind;

    [Fact]
    public async Task MeasuresWhatTheResultKeepsAndLetsItGo()
    {
        var sample = await Timing.MeasureAsync(_ => ValueTask.FromResult(new RunTally(1, 0, Complete: true, Kept: new KeptResult(new byte[Held]))), CancellationToken.None);

        Assert.InRange(sample.KeptBytes, Held - Tolerance, Held + Tolerance);
        Assert.Null(sample.Tally.Kept);
    }

    /// <summary>
    /// A run leaves buffers in shared pools for the next one to rent, which outlive it but are not
    /// what its result keeps. Here the run keeps such a buffer in a field of its own.
    /// </summary>
    [Fact]
    public async Task WhatTheRunLeavesBehindIsNotCountedAsKept()
    {
        try
        {
            var sample = await Timing.MeasureAsync(
                _ =>
                {
                    _leftBehind = new byte[Held];
                    return ValueTask.FromResult(new RunTally(1, 0, Complete: true, Kept: new KeptResult(new byte[Held / 4])));
                },
                CancellationToken.None);

            Assert.InRange(sample.KeptBytes, (Held / 4) - Tolerance, (Held / 4) + Tolerance);
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
    public async Task MeasuresTheProcessorTimeOfTheRunAlone()
    {
        Spin(TimeSpan.FromMilliseconds(400));

        var idle = await Timing.MeasureAsync(_ => ValueTask.FromResult(new RunTally(1, 0, Complete: true)), CancellationToken.None);
        var busy = await Timing.MeasureAsync(
            _ =>
            {
                Spin(TimeSpan.FromMilliseconds(200));
                return ValueTask.FromResult(new RunTally(1, 0, Complete: true));
            },
            CancellationToken.None);

        Assert.InRange(idle.ProcessorTime, TimeSpan.Zero, TimeSpan.FromMilliseconds(150));
        Assert.True(busy.ProcessorTime >= TimeSpan.FromMilliseconds(200), $"{busy.ProcessorTime} of processor time");
    }

    [Fact]
    public async Task ARunThatKeepsNothingKeepsNothing() =>
        Assert.Equal(0, (await Timing.MeasureAsync(_ => ValueTask.FromResult(new RunTally(1, 0, Complete: true)), CancellationToken.None)).KeptBytes);

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
