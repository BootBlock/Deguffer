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

    /// <summary>
    /// Build the Explore tree for one folder by walking it: the route an unelevated Explore takes,
    /// assembling the pictures of the tree so far that it draws while it walks, unless told not to.
    /// </summary>
    ExploreWalk,

    /// <summary>
    /// Find build outputs in one folder through <c>SourceTreeWalk</c>, with every name the source
    /// folder rows look for: what an approved source folder costs a scan that cannot read the table.
    /// </summary>
    Sources,

    /// <summary>
    /// Measure one folder as the hard-link-aware scan measures a store, reading each file's link
    /// count and sizes by name or through a handle: what a pnpm store or the driver store costs.
    /// </summary>
    Links,
}

internal static class RouteFacts
{
    /// <summary>Whether the route reads a volume's file table, which needs administrator rights.</summary>
    public static bool ReadsTable(this Route route) => route is not (Route.Walk or Route.ExploreWalk or Route.Sources or Route.Links);

    /// <summary>
    /// Whether the route walks with the values given on the command line. The links route walks with
    /// the values a scan ships with, because what it compares is the read of each file.
    /// </summary>
    public static bool TakesWalkTuning(this Route route) => route is Route.Walk or Route.ExploreWalk or Route.Sources;

    /// <summary>What one counted item is, in the plural, as the report names it.</summary>
    public static string ItemName(this Route route) => route switch
    {
        Route.Walk or Route.ExploreWalk => "entries",
        Route.Sources => "candidates",
        Route.Links => "files",
        _ => "records",
    };
}
