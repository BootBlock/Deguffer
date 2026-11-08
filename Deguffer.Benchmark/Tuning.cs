using System.Globalization;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The values a route was measured with, stated in every result.
///
/// <para>Two results are only comparable at the same values, and a value fixed in the build can
/// change between builds, so a result from an older build would otherwise be indistinguishable from
/// one taken after a value changed. A value that becomes a setting becomes an argument here, and is
/// stated the same way.</para>
/// </summary>
internal static class Tuning
{
    /// <param name="tree">The tree a removal route builds, or each of the clean route's. No other route reads it.</param>
    /// <param name="trees">How many trees the clean route builds. No other route reads it.</param>
    public static string Describe(
        Route route, WalkTuning walk, TableTuning table, bool snapshots, LinkRead readBy, TreeShape tree, int trees)
    {
        // The shape is stated because it is the whole of what one removal result is a result for.
        if (route is Route.Remove)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"a tree of {tree.Files:N0} empty {(tree.ReadOnly ? "read-only " : string.Empty)}files in {tree.Folders + 1:N0} folders, " +
                $"{tree.Depth} deep, {tree.Fanout} to a folder, removed as a clean removes it");
        }

        if (route is Route.Clean)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{trees:N0} build folders of {tree.Files:N0} empty files in {tree.Folders + 1:N0} folders, " +
                $"removed as one plan with nothing running");
        }

        if (route is Route.Scan)
        {
            return "the values a scan uses for the drive's kind, under the shipped settings, a new scanner each run";
        }

        if (route.ReadsTable())
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{table.ReadBytes / 1024} KiB reads, {table.ReadsInFlight} in flight, parsed on {table.ParseThreads} threads, " +
                $"{table.ListReadsInFlight} list reads in flight");
        }

        if (route is Route.Folders)
        {
            return "the values a search uses for each drive's kind, under the shipped settings";
        }

        if (route is Route.Links)
        {
            return readBy is LinkRead.Name
                ? "the walk a scan ships with, each file read by name"
                : "the walk a scan ships with, each file read through a handle";
        }

        var listing = Listing(walk);

        // Stated because it is the value the Explore walk is timed both ways with.
        return route is Route.ExploreWalk
            ? listing + (snapshots ? ", snapshots on" : ", snapshots off")
            : listing;
    }

    /// <summary>
    /// The race's values: how the folder was walked, which volume's index was built beside it and
    /// where that volume is, how the build read, and whether a command ran before each part. The
    /// command itself is never stated, because it can name a path.
    /// </summary>
    public static string DescribeRace(
        Route racer, WalkTuning walk, char drive, DiskRelation relation, TableTuning table, bool buildYields, bool beforePart)
    {
        var walked = racer is Route.Walk ? "the walk" : "the search for build outputs";
        var between = beforePart ? "a command run before each part" : "nothing run between parts";
        var priority = buildYields ? "at low priority while the walk runs" : "at normal priority";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{walked}, {Listing(walk)}, raced against the index of {char.ToUpperInvariant(drive)}:, {relation.Describe()}, " +
            $"{table.ReadBytes / 1024} KiB reads {priority}, {table.ReadsInFlight} in flight, parsed on {table.ParseThreads} threads, " +
            $"{table.ListReadsInFlight} list reads in flight, {between}");
    }

    private static string Listing(WalkTuning walk) => string.Create(
        CultureInfo.InvariantCulture,
        $"{walk.Threads} folders listed at once, {walk.ListingBufferBytes / 1024} KiB listing buffer");
}