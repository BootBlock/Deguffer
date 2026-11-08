namespace Deguffer.Core.Exploring;

/// <summary>
/// What the file table's read tells a raced scan's caller: nothing while the walk beside it is
/// running, and its counts and snapshots once the walk has ended without an answer and the table is
/// the only route left answering.
///
/// <para><b>One route on screen at a time.</b> The walk counts entries towards no known total and the
/// table counts records towards its record count, so a page told both at once would flip its bar
/// between a fraction and none, and its sentence between two scans, on every report. The walk is the
/// one shown while both run, because it is the one expected to answer a folder first and its
/// snapshots are drawn from the folder rather than from a whole volume.</para>
///
/// <para><b>No snapshot is assembled while held.</b> Assembling one is a pass over every record slot
/// of the table, on a processor the walk is using, for a picture nobody would be shown. The read
/// still captures what a snapshot is assembled from, which costs nothing (<see cref="ScanSnapshots"/>),
/// so the first report after <see cref="TableAlone"/> starts one.</para>
///
/// <para>Released only after the walk has ended, so the two routes never report beside each other,
/// as <see cref="ScanSnapshots"/> requires of a scan's reports.</para>
/// </summary>
internal sealed class HeldTableReports : IProgress<ExploreProgress>
{
    private readonly IProgress<ExploreProgress> _progress;
    private volatile bool _alone;

    /// <param name="progress">The caller's progress, which the walk reports to directly.</param>
    /// <param name="snapshots">How the caller's snapshots are assembled, on either route.</param>
    public HeldTableReports(IProgress<ExploreProgress> progress, SnapshotAssembly snapshots)
    {
        _progress = progress;
        Snapshots = snapshots with { Start = assemble => _alone ? snapshots.Start(assemble) : Task.CompletedTask };
    }

    /// <summary>How the table's read assembles a snapshot: not at all until <see cref="TableAlone"/>.</summary>
    public SnapshotAssembly Snapshots { get; }

    /// <summary>The walk has ended without an answer, so the table's reports are passed on from now.</summary>
    public void TableAlone() => _alone = true;

    public void Report(ExploreProgress value)
    {
        if (_alone)
        {
            _progress.Report(value);
        }
    }
}
