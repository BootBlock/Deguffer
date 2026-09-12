namespace Deguffer.Core.Memory;

/// <summary>
/// Deguffer's own figures as Windows documents them, for checking the table against.
/// </summary>
/// <param name="CreationTime">From <c>GetProcessTimes</c>, as a FILETIME in UTC.</param>
/// <param name="PrivateWorkingSet">
/// From the documented counter, or from its documented fallback, or null where neither answered.
/// </param>
internal readonly record struct OwnProcessReference(long CreationTime, long? PrivateWorkingSet);

/// <summary>
/// Decides whether the undocumented figures in one read of the process table can be believed, by
/// comparing Deguffer's own record with what Windows documents about the same process.
///
/// <para>The precedent is <see cref="Safety.RunningProcessTable"/>, which checks its undocumented
/// offsets against its own working directory. The difference is that one of these two figures moves:
/// a working set changes between the read of the table and the read of the counter, so it is compared
/// within a margin, while the creation time never changes and is compared exactly.</para>
///
/// <para><b>Two verdicts are kept, and the rest are asked again.</b> A layout found sound belongs to the
/// Windows build, and the architecture to the build of Deguffer, and neither can change while the
/// process runs. Any other verdict may be the table and the counter landing far apart for a moment,
/// so the next read checks again rather than keeping the figures off for good.</para>
/// </summary>
/// <param name="ownProcessId">This process, whose record the table is checked by.</param>
/// <param name="narrowOnWideWindows">
/// Whether this is a 32-bit process on 64-bit Windows. See
/// <see cref="ProcessFigures.NotOnThisArchitecture"/>.
/// </param>
internal sealed class ProcessFigureCheck(int ownProcessId, bool narrowOnWideWindows)
{
    /// <summary>
    /// The drift allowed between the table's private working set and the counter's: a quarter of the
    /// documented figure, and never less than 16 MiB.
    ///
    /// <para>Read one after the other, the two were measured within 2% of each other in a 64-bit
    /// process on one machine. A field read at the wrong offset is not near the right one at all, being
    /// a thread count, a cycle time or a timestamp, so the margin can be generous without letting one
    /// through. The floor is for a small process, whose quarter a garbage collection could cross on its
    /// own.</para>
    /// </summary>
    private const long MarginFloor = 16L * 1024 * 1024;

    private ProcessFigures? _settled;

    /// <summary>
    /// The verdict on <paramref name="table"/>.
    ///
    /// <para><paramref name="reference"/> is read only where it will be compared, so a 32-bit Deguffer
    /// on 64-bit Windows, and every read after a pass, never pays for it.</para>
    /// </summary>
    public ProcessFigures Judge(ParsedProcessTable table, Func<OwnProcessReference> reference)
    {
        if (_settled is { } settled)
        {
            return settled;
        }

        var verdict = narrowOnWideWindows ? ProcessFigures.NotOnThisArchitecture : Compare(table, reference);

        if (verdict is ProcessFigures.Checked or ProcessFigures.NotOnThisArchitecture)
        {
            _settled = verdict;
        }

        return verdict;
    }

    private ProcessFigures Compare(ParsedProcessTable table, Func<OwnProcessReference> read)
    {
        if (OwnRecord(table) is not { } own)
        {
            return ProcessFigures.OwnProcessNotListed;
        }

        var reference = read();

        if (own.CreationTime != reference.CreationTime)
        {
            return ProcessFigures.CreationTimeDisagrees;
        }

        if (reference.PrivateWorkingSet is not { } documented)
        {
            return ProcessFigures.NothingToCheckAgainst;
        }

        return Math.Abs(own.PrivateWorkingSet - documented) <= Math.Max(MarginFloor, documented / 4)
            ? ProcessFigures.Checked
            : ProcessFigures.PrivateWorkingSetDisagrees;
    }

    private ProcessRecord? OwnRecord(ParsedProcessTable table)
    {
        foreach (var record in table.Records)
        {
            if (record.ProcessId == ownProcessId)
            {
                return record;
            }
        }

        return null;
    }
}
