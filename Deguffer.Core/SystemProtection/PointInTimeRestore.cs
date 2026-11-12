namespace Deguffer.Core.SystemProtection;

/// <summary>
/// What Windows records of point-in-time restore, the feature of Windows 11 24H2 and later that keeps a
/// shadow copy of the whole system volume every day in the storage System Restore uses, under the same
/// limit.
///
/// <para><b>Read from where the feature keeps its own records.</b> Its library names
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\Recovery\PITR</c>, with a <c>Settings</c> key
/// beside a <c>Snapshots</c> key, and its log reports the second missing on a machine that has none.
/// Neither is documented. Whether the feature is on cannot be read there: a setting the user never
/// changed is absent, and the feature then decides from the edition, the drive's size and whether the
/// machine is managed. What it has recorded can be read, and that is what the row needs.</para>
/// </summary>
/// <param name="Installed">
/// Whether this Windows has the feature at all, so that its limit can also be set in Settings.
/// </param>
/// <param name="Snapshots">What it has recorded of its own snapshots.</param>
/// <param name="Failure">Why the record could not be read, written for the user, or null.</param>
public sealed record PointInTimeRestore(bool Installed, PointInTimeSnapshots Snapshots, string? Failure = null)
{
    /// <summary>A Windows from before the feature.</summary>
    public static PointInTimeRestore Absent { get; } = new(Installed: false, PointInTimeSnapshots.None);

    /// <summary>The feature, with no snapshot recorded.</summary>
    public static PointInTimeRestore Idle { get; } = new(Installed: true, PointInTimeSnapshots.None);

    /// <summary>The feature, with snapshots recorded.</summary>
    public static PointInTimeRestore Keeping { get; } = new(Installed: true, PointInTimeSnapshots.Recorded);

    /// <summary>The feature's record, which Windows would not show.</summary>
    public static PointInTimeRestore Unread(string why) => new(Installed: true, PointInTimeSnapshots.Unread, why);

    /// <summary>
    /// Whether its shadow copies may be on the machine now. A record that could not be read is no
    /// evidence that there are none.
    /// </summary>
    public bool MayKeepSnapshots => Snapshots is not PointInTimeSnapshots.None;
}

/// <summary>What point-in-time restore has recorded of its own snapshots.</summary>
public enum PointInTimeSnapshots
{
    /// <summary>It records none, or this Windows has no such feature.</summary>
    None,

    /// <summary>It records at least one.</summary>
    Recorded,

    /// <summary>Windows would not show its record.</summary>
    Unread,
}
