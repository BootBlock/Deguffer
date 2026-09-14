using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Answers with the facts a test wrote, and records what it was asked about, so §7.2's rule that
/// nothing is asked of Windows for a row nobody selected can be held to.
/// </summary>
internal sealed class FakeProcessFactSource(ProcessFacts facts) : IProcessFactSource
{
    private readonly List<(int ProcessId, long CreationTime)> _asked = [];

    public IReadOnlyList<(int ProcessId, long CreationTime)> Asked => _asked;

    public ProcessFacts Read(int processId, long creationTime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _asked.Add((processId, creationTime));

        return facts;
    }
}
