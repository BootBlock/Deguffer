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
/// </summary>
internal static class ProcessFigureCheck
{
    /// <summary>
    /// The drift two readings of one process's private working set may show and still agree: a quarter
    /// of the documented figure, and never less than 16 MiB.
    ///
    /// <para>Two readings a moment apart were measured within 2% of each other. A field read at the
    /// wrong offset is not near the right one at all, being a thread count, a cycle time or a timestamp,
    /// so the margin can be generous without letting one through. The floor is for a small process, whose
    /// quarter a garbage collection could cross on its own.</para>
    /// </summary>
    private const long MarginFloor = 16L * 1024 * 1024;

    public static ProcessFigures Judge(
        ParsedProcessTable table, int ownProcessId, OwnProcessReference reference, bool narrowOnWideWindows)
    {
        if (narrowOnWideWindows)
        {
            return ProcessFigures.NotOnThisArchitecture;
        }

        if (OwnRecord(table, ownProcessId) is not { } own)
        {
            return ProcessFigures.OwnProcessNotListed;
        }

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

    private static ProcessRecord? OwnRecord(ParsedProcessTable table, int ownProcessId)
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
