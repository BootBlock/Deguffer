namespace Deguffer.Core.Memory;

/// <summary>
/// Whether a snapshot's two undocumented per-process figures may be used, and where not, why.
///
/// <para>The private working set and the creation time sit in the part of
/// <c>SYSTEM_PROCESS_INFORMATION</c> that <c>winternl.h</c> declares only as reserved bytes. A layout
/// that moved would read as plausible numbers rather than as an error, so both are used only once
/// <see cref="ProcessFigureCheck"/> has found them agreeing with Deguffer's own process. They are
/// on or off together: they share one reserved block, so either one disagreeing says the block is
/// not where it was expected, and the other cannot be trusted either (§7.2).</para>
///
/// <para>Every value but <see cref="Checked"/> turns them off, and the zero value is one of those, so
/// a verdict nobody set reads as off.</para>
/// </summary>
public enum ProcessFigures
{
    /// <summary>
    /// Neither the documented counter nor its documented fallback answered for Deguffer's own
    /// process, so there was nothing to check the private working set against.
    /// </summary>
    NothingToCheckAgainst = 0,

    /// <summary>The table did not list Deguffer's own process, the one record whose figures are known.</summary>
    OwnProcessNotListed,

    /// <summary>The creation time read for Deguffer's own process is not the one Windows documents.</summary>
    CreationTimeDisagrees,

    /// <summary>
    /// The private working set read for Deguffer's own process is further from the documented counter
    /// than two readings a moment apart can drift.
    /// </summary>
    PrivateWorkingSetDisagrees,

    /// <summary>
    /// A 32-bit Deguffer on 64-bit Windows. It is handed the 32-bit form of the table, whose sizes are
    /// 32 bits wide, so a process holding more than 4 GB cannot be described in it at all.
    /// </summary>
    NotOnThisArchitecture,

    /// <summary>Both figures agreed with Deguffer's own process.</summary>
    Checked,
}

/// <summary>One process, as the kernel's process table describes it.</summary>
/// <param name="ProcessId">The identifier Windows gave it. Windows reuses these once a process has gone.</param>
/// <param name="ParentProcessId">
/// The identifier of the process that created it, as recorded at the time. A hint rather than a link:
/// that process may have exited since and its identifier gone to a later one, which only
/// <see cref="CreationTime"/> can tell apart.
/// </param>
/// <param name="Name">The image name. Empty where Windows gives none, as it does for the idle process.</param>
/// <param name="CommitCharge">
/// Private bytes the process has committed, whether in memory, compressed or paged out. Documented,
/// as <c>PagefileUsage</c>.
/// </param>
/// <param name="PrivateWorkingSet">
/// Bytes in memory that no other process can use, or null where <see cref="ProcessFigures"/> turned
/// the figure off. Never zero in place of off, because zero reads as a process holding nothing.
/// </param>
/// <param name="CreationTime">
/// When the kernel created the process, as a FILETIME in UTC and exact to the tick, or null where the
/// figure is off. Exact, because it is compared rather than shown: a parent is the process holding
/// that identifier only if it was created no later than its child.
/// </param>
public sealed record ProcessMemory(
    int ProcessId,
    int ParentProcessId,
    string Name,
    long CommitCharge,
    long? PrivateWorkingSet,
    long? CreationTime);

/// <summary>Every process one read of the process table returned.</summary>
/// <param name="Processes">In the order Windows listed them.</param>
/// <param name="Figures">Whether the undocumented figures in <paramref name="Processes"/> are on.</param>
/// <param name="Complete">
/// False where the walk met a record it could not read inside the buffer Windows returned, and stopped
/// there. The processes after that point are missing, so every total drawn from this table is short.
/// </param>
public sealed record ProcessMemoryTable(
    IReadOnlyList<ProcessMemory> Processes,
    ProcessFigures Figures,
    bool Complete)
{
    /// <summary>
    /// The table as a consumer may see it: the undocumented figures carried through where the check
    /// passed, and null in every record where it did not.
    /// </summary>
    internal static ProcessMemoryTable From(ParsedProcessTable parsed, ProcessFigures figures)
    {
        var usable = figures == ProcessFigures.Checked;
        var processes = new ProcessMemory[parsed.Records.Count];

        for (var i = 0; i < processes.Length; i++)
        {
            var record = parsed.Records[i];

            processes[i] = new ProcessMemory(
                record.ProcessId,
                record.ParentProcessId,
                record.Name,
                record.CommitCharge,
                usable ? record.PrivateWorkingSet : null,
                usable ? record.CreationTime : null);
        }

        return new ProcessMemoryTable(processes, figures, parsed.Complete);
    }
}
