using Deguffer.Core.Execution;

namespace Deguffer.Core.Safety;

/// <summary>
/// §5.3 — a tool's own processes holding its cache open is not a reason to refuse, but it is a
/// reason to say so before the user confirms.
/// </summary>
public static class RunningProcessNotice
{
    /// <summary>The warning to attach to a plan, or null if nothing conflicting is running.</summary>
    public static PlanNote? For(IProcessInspector inspector, IReadOnlyList<string> processNames)
    {
        var running = inspector.FindRunning(processNames);
        if (running.Count == 0)
        {
            return null;
        }

        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running. " +
            "Anything held open will be left in place rather than removed.");
    }
}
