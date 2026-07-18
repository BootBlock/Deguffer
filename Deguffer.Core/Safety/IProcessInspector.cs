using System.Diagnostics;

namespace Deguffer.Core.Safety;

/// <summary>
/// §5.3: a locked file is the OS protecting live state. Before planning, ask which of a tool's
/// processes are running so the plan can warn rather than half-delete a tree in use.
/// </summary>
public interface IProcessInspector
{
    /// <summary>
    /// Of <paramref name="names"/> (process names without extension, case-insensitive), those
    /// currently running.
    /// </summary>
    IReadOnlyList<string> FindRunning(IEnumerable<string> names);

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
