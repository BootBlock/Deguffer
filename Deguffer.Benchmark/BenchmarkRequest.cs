using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// What to time, read from the command line.
/// </summary>
/// <param name="Path">
/// For a route that reads the table, the root of the volume, such as <c>C:\</c>, or for
/// <see cref="Route.ExploreFolder"/> the folder. For a route that walks, the folder. A folder is
/// fully qualified. Never printed: see <see cref="MeasuredPlace"/>.
/// </param>
/// <param name="Runs">How many times to run the route. The first is reported apart from the rest.</param>
/// <param name="Walk">The values the routes that walk run with. The table routes take none of them.</param>
/// <param name="Table">The values the table routes run with. The routes that walk take none of them.</param>
/// <param name="Snapshots">
/// Whether the Explore walk assembles the pictures of the tree so far that Explore draws while it
/// walks. Timed both ways, the difference is what they cost the walk. No other route takes any.
/// </param>
/// <param name="ReadBy">How the links route reads each file. No other route reads any.</param>
internal sealed record BenchmarkRequest(
    Route Route, string Path, int Runs, WalkTuning Walk, TableTuning Table, bool Snapshots, LinkRead ReadBy)
{
    public const int DefaultRuns = 5;

    public const int MaximumRuns = 1000;

    public const string Usage =
        """
        Usage: Deguffer.Benchmark <route> <target> [--runs N] [options]

        Routes:
          table <drive>     Read the volume's file table end to end, parse only. Elevated.
          index <drive>     Build the file-table index the deletion path measures from. Elevated.
          explore <drive>   Build the Explore tree for the volume from its file table. Elevated.
          explorefolder <folder>
                            Build the Explore tree for the folder from its volume's file
                            table, as an elevated Explore of a folder does. Elevated.
          walk <folder>     Walk the folder as an unelevated scan does.
          measure <folder>  Measure the folder as an unelevated scan does: the walk, and
                            what the scan adds up for each entry.
          explorewalk <folder>
                            Build the Explore tree for the folder by walking it, as an
                            unelevated Explore does.
          sources <folder>  Find build outputs in the folder as an unelevated scan does.
          links <folder>    Measure the folder as a hard-linked store such as pnpm's is
                            measured, reading each file's link count and sizes.

        --runs N               How many times to run the route, 1 to 1000. The default is 5. The
                               first run is reported apart from the others.
        --threads N            Walk, measure, explorewalk and sources only. How many folders
                               are listed at once, 1 to 64. The default is what a scan uses.
        --listing-buffer KiB   Walk, measure, explorewalk and sources only. How many KiB of
                               entries each listing asks Windows for, 4 to 1024. The default
                               is what a scan uses.
        --read-by name|handle  Links only. Whether each file is read by name, as a scan does
                               where the file system answers that, or through a handle opened
                               on it. The default is name.
        --snapshots on|off     Explore walk only. Whether it assembles the pictures of the tree
                               so far that Explore draws while it walks. The default is on.
        --read-size KiB        Table routes only. How many KiB of records each read asks for, 4
                               to 16384.
        --reads-in-flight N    Table routes only. How many reads are outstanding at once, 1 to 32.
        --parse-threads N      Table routes only. How many threads parse records, 1 to 64.

        A table route's defaults are what a scan uses on a drive of unknown kind. The Explore
        routes also report what the finished tree keeps on the managed heap.

        It only reads. Nothing on the drive is written, moved or deleted.
        """;

    /// <summary>The volume's drive letter. Meaningful only for a route that reads the table.</summary>
    public char Drive => Path[0];

    /// <summary>
    /// Read <paramref name="args"/>, or return null with what was wrong in
    /// <paramref name="error"/>.
    /// </summary>
    public static BenchmarkRequest? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;

        if (args.Count < 2)
        {
            error = "Name a route and a target.";
            return null;
        }

        // By name alone. Enum.TryParse would also take "1" and "table, index", neither of which names
        // a route.
        var named = Enum.GetValues<Route>()
            .Where(r => string.Equals(r.ToString(), args[0], StringComparison.OrdinalIgnoreCase))
            .Cast<Route?>()
            .SingleOrDefault();

        if (named is not { } route)
        {
            error = "The route must be table, index, explore, explorefolder, walk, measure, explorewalk, sources or links.";
            return null;
        }

        var runs = DefaultRuns;
        var threads = WalkTuning.Default.Threads;
        var bufferKiB = WalkTuning.Default.ListingBufferBytes / 1024;
        var readKiB = UnknownDrive.ReadBytes / 1024;
        var readsInFlight = UnknownDrive.ReadsInFlight;
        var parseThreads = UnknownDrive.ParseThreads;
        var snapshots = true;
        var readBy = LinkRead.Name;

        for (var i = 2; i < args.Count; i += 2)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;

            var accepted = args[i] switch
            {
                "--runs" => TryNumber(value, 1, MaximumRuns, out runs),
                "--threads" when route.TakesWalkTuning() =>
                    TryNumber(value, WalkTuning.MinimumThreads, WalkTuning.MaximumThreads, out threads),
                "--listing-buffer" when route.TakesWalkTuning() => TryNumber(
                    value, WalkTuning.MinimumListingBuffer / 1024, WalkTuning.MaximumListingBuffer / 1024, out bufferKiB),
                "--read-size" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumReadBytes / 1024, TableTuning.MaximumReadBytes / 1024, out readKiB),
                "--reads-in-flight" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumReadsInFlight, TableTuning.MaximumReadsInFlight, out readsInFlight),
                "--parse-threads" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumParseThreads, TableTuning.MaximumParseThreads, out parseThreads),
                "--snapshots" when route is Route.ExploreWalk => TryOnOff(value, out snapshots),
                "--read-by" when route is Route.Links => TryReadBy(value, out readBy),
                _ => false,
            };

            if (!accepted)
            {
                error =
                    $"--runs takes a number from 1 to {MaximumRuns}. For walk, measure, explorewalk and sources, --threads takes a number from " +
                    $"{WalkTuning.MinimumThreads} to {WalkTuning.MaximumThreads} and --listing-buffer from " +
                    $"{WalkTuning.MinimumListingBuffer / 1024} to {WalkTuning.MaximumListingBuffer / 1024}. " +
                    $"For the table routes, --read-size takes a number from " +
                    $"{TableTuning.MinimumReadBytes / 1024} to {TableTuning.MaximumReadBytes / 1024}, " +
                    $"--reads-in-flight a number from {TableTuning.MinimumReadsInFlight} to " +
                    $"{TableTuning.MaximumReadsInFlight} and --parse-threads from " +
                    $"{TableTuning.MinimumParseThreads} to {TableTuning.MaximumParseThreads}. " +
                    "For the Explore walk, --snapshots takes on or off. For links, --read-by takes name or handle. " +
                    "Nothing else is accepted after the target.";
                return null;
            }
        }

        var path = route.TargetsVolume() ? VolumeRoot(args[1]) : Folder(args[1]);
        if (path is null || (route.ReadsTable() && !VolumePath.TryParse(path, out _)))
        {
            error = route.TargetsVolume() ? "The target must be a drive letter, such as C or C:."
                : route.ReadsTable() ? "The target must be a folder on a drive with a letter, such as C:\\Users."
                : "The target must be a folder.";
            return null;
        }

        return new BenchmarkRequest(
            route,
            path,
            runs,
            new WalkTuning(threads, bufferKiB * 1024),
            new TableTuning(readKiB * 1024, readsInFlight, parseThreads),
            snapshots,
            readBy);
    }

    /// <summary>
    /// What a scan reads the table with on a drive of unknown kind: the values from before they could
    /// be set, and the parse threads Auto gives the machine.
    /// </summary>
    public static TableTuning UnknownDrive { get; } =
        VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Unknown).Table;

    private static bool TryNumber(string? text, int minimum, int maximum, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= minimum
        && number <= maximum;

    private static bool TryReadBy(string? text, out LinkRead readBy)
    {
        readBy = text is "handle" ? LinkRead.Handle : LinkRead.Name;
        return text is "name" or "handle";
    }

    private static bool TryOnOff(string? text, out bool on)
    {
        on = text is "on";
        return text is "on" or "off";
    }

    /// <summary>
    /// <c>C</c>, <c>C:</c> or <c>C:\</c> as the volume's root, or null. The table belongs to a
    /// volume rather than to a folder, so a folder here would be a request this route cannot honour.
    /// </summary>
    private static string? VolumeRoot(string target)
    {
        var letter = target.TrimEnd('\\');
        if (letter.Length == 2 && letter[1] == ':')
        {
            letter = letter[..1];
        }

        return letter.Length == 1 && char.IsAsciiLetter(letter[0])
            ? $"{char.ToUpperInvariant(letter[0])}:\\"
            : null;
    }

    /// <summary>
    /// The folder fully qualified, or null where Windows will not accept the text as a path. A
    /// relative folder is resolved against the working directory, which is the one the person
    /// running this typed it in.
    /// </summary>
    private static string? Folder(string target)
    {
        try
        {
            return System.IO.Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
