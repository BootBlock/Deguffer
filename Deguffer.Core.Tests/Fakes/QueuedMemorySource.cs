using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The reads a test says the machine would answer, in order, with the last one repeating.
///
/// <para>A close is judged by comparing two of them — the machine as the first message was posted
/// and the machine as the watch ended — and this machine's own process table cannot be made to lose
/// a process on cue.</para>
/// </summary>
internal sealed class QueuedMemorySource(params MemorySnapshot[] reads) : IMemorySource
{
    private int _read;

    public int Reads => _read;

    public MemorySnapshot Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = reads[Math.Min(_read, reads.Length - 1)];
        _read++;

        return snapshot;
    }
}
