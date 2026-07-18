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
}

/// <inheritdoc />
public sealed class ProcessInspector : IProcessInspector
{
    public static readonly ProcessInspector Default = new();

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (wanted.Contains(process.ProcessName))
                {
                    found.Add(process.ProcessName);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and inspection. Normal; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return [.. found];
    }
}
