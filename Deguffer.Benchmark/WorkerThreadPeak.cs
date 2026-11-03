using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// The most threads a <see cref="WorkerThreads"/> held at once while a run was timed, read every
/// millisecond on a thread of its own.
///
/// <para><b>Sampled rather than counted</b>, so the removal's code carries nothing for the benchmark
/// (G3). A worker thread lives for <see cref="WorkerThreads.IdleLifetime"/> after its last work, so
/// once started it is still there at the next sample, and the count can only be missed going up
/// between two samples, which then see the higher count.</para>
/// </summary>
internal sealed class WorkerThreadPeak
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1);

    private readonly WorkerThreads _threads;
    private readonly Thread _sampler;
    private volatile bool _stopped;
    private int _peak;

    private WorkerThreadPeak(WorkerThreads threads)
    {
        _threads = threads;
        _peak = threads.Count;
        _sampler = new Thread(Sample) { IsBackground = true, Name = "Worker thread sampler" };
        _sampler.Start();
    }

    public static WorkerThreadPeak Watch(WorkerThreads threads) => new(threads);

    /// <summary>Stop sampling, and return the peak, with the count as it is now among the samples.</summary>
    public int Stop()
    {
        _stopped = true;
        _sampler.Join();

        return Math.Max(_peak, _threads.Count);
    }

    private void Sample()
    {
        while (!_stopped)
        {
            _peak = Math.Max(_peak, _threads.Count);
            Thread.Sleep(Interval);
        }
    }
}
