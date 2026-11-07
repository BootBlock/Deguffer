using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Declares which of a tool's processes are "running", for the §5.3 warning path.
///
/// <para><b>Answers by the real inspector's contract, snapshot included.</b> A name query answers from
/// a snapshot of the declared machine taken by the first query after <see cref="Invalidate"/>, and a
/// change declared after it is not seen until <see cref="Invalidate"/>, or until
/// <see cref="DiscardOlderThan"/> finds the snapshot older than its bound, measured on a clock only
/// <see cref="PassTheReadingLimit"/> moves. A fake
/// that answered live would let a check at the clean stop reading the machine again with no test
/// going red. <see cref="Probe"/> answers live, as the real one does.</para>
/// </summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProcessLiveness> _processes = [];
    private readonly ConcurrentDictionary<int, int> _probes = new();
    private readonly List<TimeSpan> _ageLimits = [];
    private readonly List<string> _running = [.. running];
    private readonly List<StartedProcess> _started = [];
    private TimedReading<Machine>? _snapshot;

    public static FakeProcessInspector NothingRunning => new();

    /// <summary>
    /// What a snapshot's age is measured by. It moves only by <see cref="PassTheReadingLimit"/>, so a
    /// test that changes the machine between the preview and the clean says the preview stayed on
    /// screen past the limit.
    /// </summary>
    private readonly ManualTimeProvider _clock = new();

    /// <summary>
    /// Move the snapshot's clock past <see cref="IUseCheck.ReadingLimit"/>, as a preview left on screen
    /// longer than that, or a command that runs longer, moves the real one. A check asked after it
    /// reads the machine again.
    /// </summary>
    public FakeProcessInspector PassTheReadingLimit()
    {
        _clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromTicks(1));
        return this;
    }

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// How many times <see cref="Probe"/> asked about <paramref name="processId"/>, so a test can count
    /// the readings of a list that probes every process it names.
    /// </summary>
    public int ProbeCount(int processId) => _probes.GetValueOrDefault(processId);

    /// <summary>The age each <see cref="DiscardOlderThan"/> was asked with, in order.</summary>
    public IReadOnlyList<TimeSpan> AgeLimits => _ageLimits;

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
    /// Declare a program running, seen from the next snapshot, so a test can start one between the
    /// preview and the clean, pass the reading limit, and show the clean asking again.
    /// </summary>
    public FakeProcessInspector WithRunning(string name)
    {
        _running.Add(name);
        return this;
    }

    /// <summary>
    /// Declare a process running, seen from the next snapshot, that was created at <paramref name="startedAt"/>, so a
    /// test can tell one process of a name from another. A process declared by name alone answers
    /// <see cref="FindRunningProcesses"/> with no creation time, as a service does on an unelevated run.
    /// </summary>
    public FakeProcessInspector WithRunning(string name, DateTimeOffset startedAt)
    {
        _started.Add(new StartedProcess(name, startedAt));
        return this;
    }

    /// <summary>
    /// Declare a program closed, seen from the next snapshot, so a test can close one between the
    /// preview and the clean, pass the reading limit, and show the clean asking again.
    /// </summary>
    public FakeProcessInspector WithoutRunning(string name)
    {
        _running.RemoveAll(running => running.Equals(name, StringComparison.OrdinalIgnoreCase));
        _started.RemoveAll(process => process.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names)
    {
        var machine = Snapshot();

        return [.. names.Intersect([.. machine.Running, .. machine.Started.Select(process => process.Name)], StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The processes the snapshot names, each kept only where it is still declared running now, as the
    /// real inspector takes its ids from the snapshot and probes each one at the moment of the call.
    /// </summary>
    public IReadOnlyList<StartedProcess> FindRunningProcesses(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var machine = Snapshot();

        lock (_gate)
        {
            return
            [
                .. machine.Running
                    .Where(name => wanted.Contains(name) && _running.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .Select(name => new StartedProcess(name, StartedAt: null)),
                .. machine.Started.Where(process => wanted.Contains(process.Name) && _started.Contains(process)),
            ];
        }
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

        _probes.AddOrUpdate(processId, 1, (_, count) => count + 1);
        return _processes.GetValueOrDefault(processId, ProcessLiveness.NotRunning);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            InvalidateCount++;
            _snapshot?.Forget();
        }
    }

    /// <summary>Recorded, and acted on as the real inspector acts on its own.</summary>
    public void DiscardOlderThan(TimeSpan maxAge)
    {
        lock (_gate)
        {
            _ageLimits.Add(maxAge);
            _snapshot?.ForgetOlderThan(maxAge);
        }
    }

    /// <summary>The declared machine as the snapshot held saw it, or as it is now where none is held.</summary>
    private Machine Snapshot()
    {
        lock (_gate)
        {
            return (_snapshot ??= new(_clock)).Get(() => new Machine([.. _running], [.. _started]));
        }
    }

    private sealed record Machine(IReadOnlyList<string> Running, IReadOnlyList<StartedProcess> Started);
}
