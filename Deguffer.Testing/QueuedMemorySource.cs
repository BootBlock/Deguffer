using Deguffer.Core.Memory;

namespace Deguffer.Testing;

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

    /// <summary>
    /// The read from which Windows refuses to describe the machine, counting from one. A close
    /// cannot be recalled, so what happens to the report when the read after the watch fails is
    /// something a test has to be able to arrange.
    /// </summary>
    public int RefusesFrom { get; init; } = int.MaxValue;

    public MemorySnapshot Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _read++;

        return _read >= RefusesFrom
            ? throw new System.ComponentModel.Win32Exception(5)
            : reads[Math.Min(_read - 1, reads.Length - 1)];
    }
}
