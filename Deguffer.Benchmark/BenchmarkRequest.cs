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
/// For a route that reads the table, the root of the volume, such as <c>C:\</c>. For the walk, the
/// folder, fully qualified. Never printed: see <see cref="MeasuredPlace"/>.
/// </param>
/// <param name="Runs">How many times to run the route. The first is reported apart from the rest.</param>
/// <param name="Walk">The values the walk runs with. The table routes take none of them.</param>
/// <param name="Table">The values the table routes run with. The walk takes none of them.</param>
internal sealed record BenchmarkRequest(Route Route, string Path, int Runs, WalkTuning Walk, TableTuning Table)
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
          walk <folder>     Walk the folder as an unelevated scan does.

        --runs N               How many times to run the route, 1 to 1000. The default is 5. The
                               first run is reported apart from the others.
        --threads N            Walk only. How many folders are listed at once, 1 to 64. The
                               default is what a scan uses.
        --listing-buffer KiB   Walk only. How many KiB of entries each listing asks Windows for,
                               4 to 1024. The default is what a scan uses.
        --read-size KiB        Table routes only. How many KiB of records each read asks for, 4
                               to 16384.
        --reads-in-flight N    Table routes only. How many reads are outstanding at once, 1 to 32.
        --parse-threads N      Table routes only. How many threads parse records, 1 to 64.

        A table route's defaults are what a scan uses on a drive of unknown kind.

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
            error = "The route must be table, index, explore or walk.";
            return null;
        }

        var runs = DefaultRuns;
        var threads = WalkTuning.Default.Threads;
        var bufferKiB = WalkTuning.Default.ListingBufferBytes / 1024;
        var readKiB = UnknownDrive.ReadBytes / 1024;
        var readsInFlight = UnknownDrive.ReadsInFlight;
        var parseThreads = UnknownDrive.ParseThreads;

        for (var i = 2; i < args.Count; i += 2)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;

            var accepted = args[i] switch
            {
                "--runs" => TryNumber(value, 1, MaximumRuns, out runs),
                "--threads" when !route.ReadsTable() =>
                    TryNumber(value, WalkTuning.MinimumThreads, WalkTuning.MaximumThreads, out threads),
                "--listing-buffer" when !route.ReadsTable() => TryNumber(
                    value, WalkTuning.MinimumListingBuffer / 1024, WalkTuning.MaximumListingBuffer / 1024, out bufferKiB),
                "--read-size" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumReadBytes / 1024, TableTuning.MaximumReadBytes / 1024, out readKiB),
                "--reads-in-flight" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumReadsInFlight, TableTuning.MaximumReadsInFlight, out readsInFlight),
                "--parse-threads" when route.ReadsTable() => TryNumber(
                    value, TableTuning.MinimumParseThreads, TableTuning.MaximumParseThreads, out parseThreads),
                _ => false,
            };

            if (!accepted)
            {
                error =
                    $"--runs takes a number from 1 to {MaximumRuns}. For the walk, --threads takes a number from " +
                    $"{WalkTuning.MinimumThreads} to {WalkTuning.MaximumThreads} and --listing-buffer from " +
                    $"{WalkTuning.MinimumListingBuffer / 1024} to {WalkTuning.MaximumListingBuffer / 1024}. " +
                    $"For the table routes, --read-size takes a number from " +
                    $"{TableTuning.MinimumReadBytes / 1024} to {TableTuning.MaximumReadBytes / 1024}, " +
                    $"--reads-in-flight a number from {TableTuning.MinimumReadsInFlight} to " +
                    $"{TableTuning.MaximumReadsInFlight} and --parse-threads from " +
                    $"{TableTuning.MinimumParseThreads} to {TableTuning.MaximumParseThreads}. " +
                    "Nothing else is accepted after the target.";
                return null;
            }
        }

        var path = route.ReadsTable() ? VolumeRoot(args[1]) : Folder(args[1]);
        if (path is null)
        {
            error = route.ReadsTable()
                ? "The target must be a drive letter, such as C or C:."
                : "The target must be a folder.";
            return null;
        }

        return new BenchmarkRequest(
            route,
            path,
            runs,
            new WalkTuning(threads, bufferKiB * 1024),
            new TableTuning(readKiB * 1024, readsInFlight, parseThreads));
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
