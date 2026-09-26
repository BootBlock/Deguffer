using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>Declares which of a tool's processes are "running", for the §5.3 warning path.</summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    private readonly Dictionary<int, ProcessLiveness> _processes = [];
    private readonly List<string> _running = [.. running];

    public static FakeProcessInspector NothingRunning => new();

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// Declare what one id answers, so a provider reading a file that names a process can be shown
    /// keeping the file for a running process, offering it for an ended one, and refusing it where
    /// nothing could be established.
    /// </summary>
    public FakeProcessInspector WithProcess(int processId, ProcessLiveness liveness)
    {
        _processes[processId] = liveness;
        return this;
    }

    /// <summary>
    /// Declare a program running from now on, so a test can start one between the preview and the
    /// clean and show the clean asking again.
    /// </summary>
    public FakeProcessInspector WithRunning(string name)
    {
        _running.Add(name);
        return this;
    }

    /// <summary>
    /// Declare a program closed from now on, so a test can close one between the preview and the
    /// clean and show the clean asking again.
    /// </summary>
    public FakeProcessInspector WithoutRunning(string name)
    {
        _running.RemoveAll(running => running.Equals(name, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names) =>
        [.. names.Intersect(_running, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// What <see cref="WithProcess"/> declared for the id, and otherwise
    /// <see cref="ProcessLiveness.NotRunning"/>, as a free id answers on a real machine.
    ///
    /// <para>An id below 1 is refused as the real probe refuses it, so a provider that passes one
    /// fails here rather than reading it as a process that is not running.</para>
    /// </summary>
    public ProcessLiveness Probe(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        return _processes.GetValueOrDefault(processId, ProcessLiveness.NotRunning);
    }

    public void Invalidate() => InvalidateCount++;
}
