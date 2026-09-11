using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// <see cref="Refusals"/> accumulated from several threads at once. The removal deletes in parallel
/// and the check probes in parallel (§6.3), and a record struct summed under a lock would put a
/// contended lock on every refused file of a quarter-million-file tree.
/// </summary>
internal sealed class RefusalCounter
{
    private long _inUseFiles;
    private long _inUseBytes;
    private long _deniedFiles;
    private long _deniedBytes;

    public void Add(RefusalReason reason, long bytes)
    {
        if (reason == RefusalReason.InUse)
        {
            Interlocked.Increment(ref _inUseFiles);
            Interlocked.Add(ref _inUseBytes, bytes);
        }
        else
        {
            Interlocked.Increment(ref _deniedFiles);
            Interlocked.Add(ref _deniedBytes, bytes);
        }
    }

    /// <summary>Read once the parallel work has finished, which is the only time it is asked.</summary>
    public Refusals Total => new(
        new RefusalTally((int)Interlocked.Read(ref _inUseFiles), Interlocked.Read(ref _inUseBytes)),
        new RefusalTally((int)Interlocked.Read(ref _deniedFiles), Interlocked.Read(ref _deniedBytes)));
}
