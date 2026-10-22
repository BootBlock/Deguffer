using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>Declares which of a tool's processes are "running", for the §5.3 warning path.</summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    private readonly Dictionary<int, ProcessLiveness> _processes = [];
    private readonly List<string> _running = [.. running];
    private readonly List<StartedProcess> _started = [];

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
    /// Declare a process running from now on that was created at <paramref name="startedAt"/>, so a
    /// test can tell one process of a name from another. A process declared by name alone answers
    /// <see cref="FindRunningProcesses"/> with no creation time, as a service does on an unelevated run.
    /// </summary>
    public FakeProcessInspector WithRunning(string name, DateTimeOffset startedAt)
    {
        _started.Add(new StartedProcess(name, startedAt));
        return this;
    }

    /// <summary>
    /// Declare a program closed from now on, so a test can close one between the preview and the
    /// clean and show the clean asking again.
    /// </summary>
    public FakeProcessInspector WithoutRunning(string name)
    {
        _running.RemoveAll(running => running.Equals(name, StringComparison.OrdinalIgnoreCase));
        _started.RemoveAll(process => process.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names) =>
        [.. names.Intersect([.. _running, .. _started.Select(process => process.Name)], StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<StartedProcess> FindRunningProcesses(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. _running.Where(wanted.Contains).Select(name => new StartedProcess(name, StartedAt: null)),
            .. _started.Where(process => wanted.Contains(process.Name)),
        ];
    }

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
