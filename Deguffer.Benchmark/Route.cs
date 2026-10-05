namespace Deguffer.Benchmark;

/// <summary>
/// One route a scan can take, timed on its own so a change to one is not hidden by the cost of
/// another.
/// </summary>
internal enum Route
{
    /// <summary>
    /// Read a volume's file table end to end and parse every record, keeping nothing. The floor
    /// under both structures built from the table: what the drive and the parser cost together.
    /// </summary>
    Table,

    /// <summary>Build the <c>MftVolumeIndex</c> the deletion path measures locations from.</summary>
    Index,

    /// <summary>Build the Explore tree for a whole volume from its file table.</summary>
    Explore,

    /// <summary>
    /// Walk one folder through <c>BoundedFileWalk</c>: the route an unelevated run takes, and the
    /// one every run takes where the table declines.
    /// </summary>
    Walk,
}

internal static class RouteFacts
{
    /// <summary>Whether the route reads a volume's file table, which needs administrator rights.</summary>
    public static bool ReadsTable(this Route route) => route is not Route.Walk;

    /// <summary>What one counted item is, in the plural, as the report names it.</summary>
    public static string ItemName(this Route route) => route.ReadsTable() ? "records" : "entries";
}
