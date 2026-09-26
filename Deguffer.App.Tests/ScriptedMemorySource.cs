using Deguffer.Core.Memory;

namespace Deguffer.App.Tests;

/// <summary>
/// Answers each read with what the test says the machine would, by the read's number counting from
/// one. A page that reads twice a second meets a machine that answers, then refuses, then answers
/// again, and what it says about each of those is what this arranges.
/// </summary>
internal sealed class ScriptedMemorySource(Func<int, MemorySnapshot> read) : IMemorySource
{
    private int _reads;

    public MemorySnapshot Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return read(Interlocked.Increment(ref _reads));
    }
}
