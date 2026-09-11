using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>Declares which of a tool's processes are "running", for the §5.3 warning path.</summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    public static FakeProcessInspector NothingRunning => new();

    public int InvalidateCount { get; private set; }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names) =>
        [.. names.Intersect(running, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Every id answers <see cref="ProcessLiveness.NotRunning"/>, as a free id does on a real machine.
    ///
    /// <para>An id below 1 is refused as the real probe refuses it, so a provider that passes one
    /// fails here rather than reading it as a process that is not running.</para>
    /// </summary>
    public ProcessLiveness Probe(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        return ProcessLiveness.NotRunning;
    }

    public void Invalidate() => InvalidateCount++;
}
