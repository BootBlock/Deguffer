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
    /// Build the Explore tree for one folder from its volume's file table: the route an elevated
    /// Explore of a folder takes under the Table route, and the table's side of the race it runs
    /// under Auto. It reads the whole table, as <see cref="Explore"/> does, and keeps only the
    /// folder.
    /// </summary>
    ExploreFolder,

    /// <summary>
    /// Walk one folder through <c>BoundedFileWalk</c>: the route an unelevated run takes, and the
    /// one every run takes where the table declines.
    /// </summary>
    Walk,

    /// <summary>
    /// Measure one folder through <c>ParallelEnumerationScanner</c>: the walk above with the work an
    /// unelevated scan does with each entry it lists.
    /// </summary>
    Measure,

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

    /// <summary>
    /// Scan several folders as a duplicate search does: each volume's file table read once where it
    /// can be and the walk where it cannot, with the volumes on separate disks scanned at the same
    /// time. Timed against each folder alone, whose times add up to what the scan cost when it took
    /// the volumes one after another, the difference is what scanning the disks at once saves.
    /// </summary>
    Folders,

    /// <summary>
    /// Build a tree in a new folder and remove it through <c>DirectoryRemover</c>, as a clean removes
    /// a cache or a build folder: the walk, the file pass and the folder pass, timed apart. The first of
    /// the two routes that write, and they write only inside the folder they create.
    /// </summary>
    Remove,

    /// <summary>
    /// Build many small trees in a new folder and remove them as one plan through <c>PlanExecutor</c>,
    /// as a clean of the <c>obj</c> row does: what each step costs around its removal.
    /// </summary>
    Clean,

    /// <summary>
    /// Build many empty folders in a new folder once, and record each as §5.6 records the paths a plan
    /// protects, as a row protecting one path for each item of the user's data does: whether each is
    /// there and whether it holds anything, asked several at once.
    /// </summary>
    Protect,

    /// <summary>
    /// Walk one folder, through the walk route or the sources route, while the index of a volume is
    /// built, started at the same moment, as Auto races the two in a scan, and each of the two alone,
    /// so the result says how much each slows the other. The build is of the folder's own volume
    /// unless another is named.
    /// </summary>
    Race,

    /// <summary>
    /// Measure one folder as an elevated scan does the first time it asks about the folder's volume:
    /// open the volume, find its table, and race the walk against the build of its index, taking
    /// whichever answers first.
    /// </summary>
    Scan,
}

internal static class RouteFacts
{
    /// <summary>Whether the route reads a volume's file table, which needs administrator rights.</summary>
    public static bool ReadsTable(this Route route) =>
        route is not (Route.Walk or Route.Measure or Route.ExploreWalk or Route.Sources or Route.Links or Route.Folders)
            && !route.Builds();

    /// <summary>Whether the route builds what it removes in a folder it creates.</summary>
    public static bool Removes(this Route route) => route is Route.Remove or Route.Clean;

    /// <summary>
    /// Whether the route builds what it times in a folder it creates, so its target must not exist
    /// yet, where every other route's must.
    /// </summary>
    public static bool Builds(this Route route) => route.Removes() || route is Route.Protect;

    /// <summary>
    /// Whether the route walks with the values given on the command line. The links route walks with
    /// the values a scan ships with, because what it compares is the read of each file.
    /// </summary>
    public static bool TakesWalkTuning(this Route route) =>
        route is Route.Walk or Route.Measure or Route.ExploreWalk or Route.Sources or Route.Race;

    /// <summary>
    /// Whether the route's target is a volume rather than a folder. A folder read from the table is
    /// still read from the whole of its volume's, and the race walks a folder beside a volume's build.
    /// </summary>
    public static bool TargetsVolume(this Route route) =>
        route.ReadsTable() && route is not (Route.ExploreFolder or Route.Race or Route.Scan);

    /// <summary>
    /// Whether the route reads the table with the values given on the command line. The scan route
    /// reads it with what a scan uses, because what it times is the scan.
    /// </summary>
    public static bool TakesTableTuning(this Route route) => route.ReadsTable() && route is not Route.Scan;

    /// <summary>
    /// Whether the route reads the table itself, so its reads and the stages of its build are what it
    /// reports. The race and the scan report what the walk beside the build took instead.
    /// </summary>
    public static bool ReportsTableReads(this Route route) => route.ReadsTable() && route is not (Route.Race or Route.Scan);

    /// <summary>
    /// Whether the route builds what a scan keeps on screen, so what it holds once built is part of
    /// its cost. See <see cref="RunSample.KeptBytes"/>.
    /// </summary>
    public static bool KeepsResult(this Route route) => route is Route.Explore or Route.ExploreFolder or Route.ExploreWalk;

    /// <summary>What one counted item is, in the plural, as the report names it.</summary>
    public static string ItemName(this Route route) => route switch
    {
        Route.Walk or Route.Measure or Route.ExploreWalk or Route.Folders or Route.Remove or Route.Scan => "entries",
        Route.Clean => "steps",
        Route.Sources => "candidates",
        Route.Links => "files",
        Route.Protect => "paths",
        _ => "records",
    };
}
