namespace Deguffer.Core.Memory;

/// <summary>
/// Whether a snapshot's two undocumented per-process figures may be used, and where not, why. Under
/// <see cref="NotOnThisArchitecture"/> the documented commit charge is off as well.
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
    /// than the two, read one after the other, can drift apart.
    /// </summary>
    PrivateWorkingSetDisagrees,

    /// <summary>
    /// A 32-bit Deguffer on 64-bit Windows, which hands such a process the table translated into its
    /// 32-bit form.
    ///
    /// <para>The private working set and the creation time are 64-bit fields in both forms, but nothing
    /// documents whether the translation carries a figure above 4 GB intact, and the check cannot show
    /// it either, because Deguffer's own figure is always below 4 GB. Measured on one machine, the two
    /// forms agreed for its four largest processes, none of which held 4 GB. So both stay off.</para>
    ///
    /// <para>The commit charge is a 32-bit field in that form, so it cannot describe more than 4 GB at
    /// all, and it is off as well.</para>
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
/// as <c>PagefileUsage</c>. Null under <see cref="ProcessFigures.NotOnThisArchitecture"/>, where the
/// table holds it in 32 bits.
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
    long? CommitCharge,
    long? PrivateWorkingSet,
    long? CreationTime)
{
    /// <summary>
    /// The process as words name it: its image name with its identifier. §7.2.1's confirmation, its
    /// report and its §5.6 evidence all name a process this way, because a machine runs several
    /// copies of one program and only the identifier tells them apart.
    /// </summary>
    public string Named => $"{Name} (process {ProcessId})";
}

/// <summary>Every process one read of the process table returned.</summary>
/// <param name="Processes">In the order Windows listed them.</param>
/// <param name="Figures">
/// Whether the undocumented figures in <paramref name="Processes"/> are on, and whether the commit
/// charge is off with them.
/// </param>
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
    /// passed, and null in every record where it did not, with the commit charge null as well where
    /// the table's form cannot hold it.
    /// </summary>
    internal static ProcessMemoryTable From(ParsedProcessTable parsed, ProcessFigures figures)
    {
        var usable = figures == ProcessFigures.Checked;
        var narrow = figures == ProcessFigures.NotOnThisArchitecture;
        var processes = new ProcessMemory[parsed.Records.Count];

        for (var i = 0; i < processes.Length; i++)
        {
            var record = parsed.Records[i];

            processes[i] = new ProcessMemory(
                record.ProcessId,
                record.ParentProcessId,
                record.Name,
                narrow ? null : record.CommitCharge,
                usable ? record.PrivateWorkingSet : null,
                usable ? record.CreationTime : null);
        }

        return new ProcessMemoryTable(processes, figures, parsed.Complete);
    }
}
