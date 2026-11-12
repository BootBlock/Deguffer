using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The fake probe's counts, which a test reads to see how often the machine would have been asked.
/// A caller that probes in parallel asks it from several threads at once, so every count has to come
/// out exact however the questions interleave.
/// </summary>
public sealed class FakePathProbeTests
{
    private const int Threads = 8;

    /// <summary>Enough questions per thread that two threads all but certainly update one count at once.</summary>
    private const int QuestionsPerThread = 64 * 320;

    /// <summary>A few paths, so the threads collide on each count, and enough that the counters grow while they do.</summary>
    private static readonly string[] Paths = [.. Enumerable.Range(0, 64).Select(index => $@"C:\Data\entry{index}")];

    [Fact]
    public async Task EveryQuestionAskedFromManyThreadsAtOnceIsCountedOnce()
    {
        var probe = new FakePathProbe();
        using var start = new Barrier(Threads);

        // Long-running, so each has a thread of its own and the barrier never waits on a pool that is
        // slow to add workers. A task, so a count corrupted into an exception fails this test rather
        // than the test host.
        var askers = Enumerable.Range(0, Threads).Select(asker => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();

                for (var at = 0; at < QuestionsPerThread; at++)
                {
                    var path = Paths[at % Paths.Length];

                    probe.ProbeFile(path);
                    probe.ProbeEntry(path);
                    probe.ProbeDirectory(path, out _);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        await Task.WhenAll(askers);

        var expected = Threads * QuestionsPerThread / Paths.Length;

        Assert.All(Paths, path => Assert.Equal(expected, probe.FileQueries[path]));
        Assert.All(Paths, path => Assert.Equal(expected, probe.EntryQueries[path]));
        Assert.All(Paths, path => Assert.Equal(expected, probe.DirectoryQueries[path]));
    }
}
