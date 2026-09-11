using System.Diagnostics;

namespace Deguffer.Core.Safety;

/// <summary>
/// §5.3: a locked file is the OS protecting live state. Before planning, ask which of a tool's
/// processes are running so the plan can warn rather than half-delete a tree in use.
///
/// <para>Asked by name or by id, because a tool leaves two kinds of evidence. A cache names no
/// process, so the question is whether anything of the tool's name is running. A file that records
/// a process id — a lock file, a session registry entry — names exactly one process, and a name says
/// nothing about it: every session of the same tool shares one.</para>
/// </summary>
public interface IProcessInspector
{
    /// <summary>
    /// Of <paramref name="names"/> (process names without extension, case-insensitive), those
    /// currently running.
    /// </summary>
    IReadOnlyList<string> FindRunning(IEnumerable<string> names);

    /// <summary>
    /// Whether the process with <paramref name="processId"/> is running, and when it was created.
    ///
    /// <para><b>Three answers, not two.</b> <see cref="ProcessState.Undetermined"/> means the
    /// question could not be answered, and a caller must not treat it as
    /// <see cref="ProcessState.NotRunning"/>.</para>
    ///
    /// <para><b>An id is not an identity.</b> Windows reuses ids, so a caller that recorded a
    /// creation time beside the id asks <see cref="ProcessLiveness.StateOfProcessStartedAt"/> of the
    /// answer, which tells a recycled id from the process the record was written about.</para>
    ///
    /// <para><b>Asked of Windows at the moment of the call</b>, never from the snapshot
    /// <see cref="Invalidate"/> discards. An id that was free when a snapshot was read can go to a
    /// new process before the question is asked, so a cached "not running" ages in the direction
    /// that deletes. <see cref="ProcessProbe"/> records the rest of that reasoning, and what each
    /// Win32 answer was measured to mean.</para>
    /// </summary>
    /// <param name="processId">
    /// The id as a file recorded it. It must be positive: the idle process at id 0 answers exactly as
    /// a free id does, so 0 would otherwise read as a process that is not running.
    /// </param>
    ProcessLiveness Probe(int processId);

    /// <summary>
    /// Discard any cached snapshot. Called once at the start of a planning pass, so every
    /// provider in that pass sees a consistent view of the machine without each one paying for
    /// its own full process-table walk.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed class ProcessInspector : IProcessInspector
{
    public static readonly ProcessInspector Default = new();

    private readonly Lock _gate = new();
    private HashSet<string>? _snapshot;

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return [];
        }

        wanted.IntersectWith(Snapshot());
        return [.. wanted];
    }

    public ProcessLiveness Probe(int processId) => ProcessProbe.Of(processId);

    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    /// <summary>
    /// <see cref="Process.GetProcesses"/> is a full-system snapshot and is far too expensive to
    /// repeat once per provider.
    /// </summary>
    private HashSet<string> Snapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and inspection. Normal; skip it.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return _snapshot = names;
        }
    }
}
