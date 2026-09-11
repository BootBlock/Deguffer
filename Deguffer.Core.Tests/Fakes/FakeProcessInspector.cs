using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Declares which of a tool's processes are "running", for the §5.3 warning path, and what a probe
/// of a process id answers.
///
/// <para>The real probe is exercised against real processes in <c>ProcessInspectorTests</c>. What this
/// proves is the other half: that a provider handed each of the three answers does something
/// different with it, on a machine where the tool that wrote the file is not installed.</para>
/// </summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    private readonly Dictionary<int, ProcessLiveness> _processes = [];

    public static FakeProcessInspector NothingRunning => new();

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Declares what a probe of <paramref name="processId"/> answers.
    ///
    /// <para>An id never declared answers <see cref="ProcessLiveness.NotRunning"/>, as a free id does
    /// on a real machine. A test proving a file is kept therefore has to declare the process that
    /// keeps it, rather than getting the refusal by accident.</para>
    /// </summary>
    public FakeProcessInspector WithProcess(int processId, ProcessLiveness liveness)
    {
        _processes[processId] = liveness;
        return this;
    }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names) =>
        [.. names.Intersect(running, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Refuses an id below 1 as the real probe does, so a provider that passes one fails here rather
    /// than reading it as a process that is not running.
    /// </summary>
    public ProcessLiveness Probe(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        return _processes.GetValueOrDefault(processId, ProcessLiveness.NotRunning);
    }

    public void Invalidate() => InvalidateCount++;
}
