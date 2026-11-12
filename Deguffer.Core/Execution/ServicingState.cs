using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What Windows servicing was doing at one moment, as <see cref="UnfinishedUpdate.HoldsEverything"/>
/// judges it: the restart keys present, and the servicing and setup processes running. Read together,
/// so the hold is never asked of a restart key from one moment and a process table from another.
/// </summary>
/// <param name="Restart">The restart keys present.</param>
/// <param name="Running">Every process of <see cref="UnfinishedUpdate.Processes"/> running, each with when it was created.</param>
public sealed record ServicingState(RestartOwed Restart, IReadOnlyList<StartedProcess> Running)
{
    /// <summary>The machine as <paramref name="servicing"/> and <paramref name="inspector"/> describe it now.</summary>
    public static ServicingState Read(IWindowsServicing servicing, IProcessInspector inspector) =>
        new(servicing.ReadRestart(), inspector.FindRunningProcesses(UnfinishedUpdate.Processes));
}
