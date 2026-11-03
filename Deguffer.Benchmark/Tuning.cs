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

        var listing = string.Create(
            CultureInfo.InvariantCulture,
            $"{walk.Threads} folders listed at once, {walk.ListingBufferBytes / 1024} KiB listing buffer");

        // Stated because it is the value the Explore walk is timed both ways with.
        return route is Route.ExploreWalk
            ? listing + (snapshots ? ", snapshots on" : ", snapshots off")
            : listing;
    }
}