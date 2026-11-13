using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;
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
/// fully qualified. For a route that takes several folders, the first of <paramref name="Folders"/>.
/// Never printed: see <see cref="MeasuredPlace"/>.
/// </param>
/// <param name="Runs">How many times to run the route. The first is reported apart from the rest.</param>
/// <param name="Walk">The values the routes that walk run with. The table routes take none of them.</param>
/// <param name="Table">The values the table routes run with. The routes that walk take none of them.</param>
/// <param name="Snapshots">
/// Whether the Explore walk assembles the pictures of the tree so far that Explore draws while it
/// walks. Timed both ways, the difference is what they cost the walk. No other route takes any.
/// </param>
/// <param name="ReadBy">How the links route reads each file. No other route reads any.</param>
/// <param name="Folders">
/// Every folder the folders route scans or the duplicates route searches, fully qualified, in the
/// order given. For any other route, <paramref name="Path"/> alone.
/// </param>
/// <param name="Tree">
/// For <see cref="Route.Remove"/>, the tree it builds and removes. For <see cref="Route.Clean"/>, each
/// of the <paramref name="Trees"/> build folders it builds and removes as one plan. No other route
/// builds any.
/// </param>
/// <param name="Trees">
/// How many trees the clean route builds, or how many empty folders the protect route builds and
/// records. One for every other route.
/// </param>
/// <param name="Drive">
/// For a route that reads the table, the volume it reads: the letter of <paramref name="Path"/>, or
/// for <see cref="Route.Race"/> the volume whose index is built beside the walk. Meaningless for
/// every other route.
/// </param>
/// <param name="Racer">
/// For <see cref="Route.Race"/>, the route that walks: <see cref="Route.Sources"/>, as discovery
/// walks, or <see cref="Route.Walk"/>. No other route reads it.
/// </param>
/// <param name="BeforePart">
/// For <see cref="Route.Race"/>, the command run before each part, such as one that empties the
/// system file cache, or null for none. No other route reads it. Never printed: see
/// <see cref="PartCommand"/>.
/// </param>
/// <param name="BuildYields">
/// For <see cref="Route.Race"/>, whether the build reads at low priority while the walk runs, as
/// Auto's build does where the two share a disk, or at normal priority throughout, as every build read
/// before it could yield. No other route reads it.
/// </param>
/// <param name="Listed">
/// For <see cref="Route.Protect"/>, whether the folders are recorded from a listing of the folder that
/// holds them, as a row that listed them while it planned records them, rather than each asked of the
/// disk. Timed both ways, the difference is what reusing the listing saves. No other route reads it.
/// </param>
/// <param name="Checksum">
/// For <see cref="Route.Duplicates"/>, the checksum the content is compared by, one this machine
/// offers. No other route reads it.
/// </param>
/// <param name="WarmCache">
/// For <see cref="Route.Duplicates"/>, whether each run starts with the checksums an untimed search
/// over the same folders kept, rather than with none. Timed both ways, the difference is what a
/// second search saves. No other route reads it.
/// </param>
internal sealed record BenchmarkRequest(
    Route Route,
    string Path,
    int Runs,
    WalkTuning Walk,
    TableTuning Table,
    bool Snapshots,
    LinkRead ReadBy,
    IReadOnlyList<string> Folders,
    TreeShape Tree,
    int Trees,
    char Drive,
    Route Racer,
    string? BeforePart,
    bool BuildYields,
    bool Listed,
    ChecksumAlgorithm Checksum,
    bool WarmCache)
{
    public const int DefaultRuns = 5;

    public const int MaximumRuns = 1000;

    public const int MaximumTrees = 100_000;

    /// <summary>
    /// The clean route's trees when told nothing about them: a clean of the <c>obj</c> row across a few
    /// hundred projects.
    /// </summary>
    public const int DefaultTrees = 500;

    /// <summary>
    /// The protect route's folders when told nothing about them: about as many as each Claude Code row
    /// was measured protecting on a machine with that data, 3,800 to 5,200 paths.
    /// </summary>
    public const int DefaultProtectedFolders = 5000;

    public const int DefaultFilesPerTree = 100;

    public const int MaximumFilesPerTree = 100_000;

    /// <summary>The tree the remove route builds when told nothing about it: a large cache, five folders deep.</summary>
    public static TreeShape DefaultTree { get; } = new(100_000, Depth: 5, Fanout: 4);

    /// <summary>
    /// Read <paramref name="args"/>, or return null with what was wrong in
    /// <paramref name="error"/>, with every drive of unknown kind.
    /// </summary>
    public static BenchmarkRequest? Parse(IReadOnlyList<string> args, out string? error) => Parse(args, ScanTuner.Shipped, out error);

    /// <summary>
    /// Read <paramref name="args"/>, or return null with what was wrong in
    /// <paramref name="error"/>.
    /// </summary>
    /// <param name="tuner">
    /// What the race walks and builds with for each drive's kind where it is told nothing else. No
    /// other route asks it.
    /// </param>
    public static BenchmarkRequest? Parse(IReadOnlyList<string> args, ScanTuner tuner, out string? error)
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
            error = "The route must be table, index, explore, explorefolder, walk, measure, explorewalk, sources, links, folders, duplicates, remove, clean, protect, race or scan.";
            return null;
        }

        var runs = DefaultRuns;
        var threads = WalkTuning.Default.Threads;
        var bufferKiB = WalkTuning.Default.ListingBufferBytes / 1024;
        var readKiB = UnknownDrive.ReadBytes / 1024;
        var readsInFlight = UnknownDrive.ReadsInFlight;
        var parseThreads = UnknownDrive.ParseThreads;
        var listReadsInFlight = UnknownDrive.ListReadsInFlight;
        var snapshots = true;
        var readBy = LinkRead.Name;
        var files = DefaultTree.Files;
        var depth = DefaultTree.Depth;
        var fanout = DefaultTree.Fanout;
        var readOnly = false;
        var trees = route switch
        {
            Route.Clean => DefaultTrees,
            Route.Protect => DefaultProtectedFolders,
            _ => 1,
        };
        var filesPerTree = DefaultFilesPerTree;
        char? build = null;
        var racer = Route.Sources;
        string? beforePart = null;
        var buildYields = true;
        var listed = false;
        var checksum = ChecksumAlgorithm.XxHash128;
        var warmCache = false;

        // Before the options, because the race's defaults depend on the drives it walks and builds
        // on, and a value an option sets has to win over them.
        if (route is Route.Race && RaceDefaults(args, tuner) is { } race)
        {
            (threads, bufferKiB, readKiB, readsInFlight, parseThreads, listReadsInFlight) = race;
        }

        // Only the routes that take several folders take more than one target, so for them the
        // options start at the first argument that is one.
        var options = route.TakesFolders() ? FirstOption(args) : 2;

        for (var i = options; i < args.Count; i += 2)
        {
            // The one option that takes no value, so the next argument is the next option.
            if (args[i] is "--read-only" && route is Route.Remove)
            {
                readOnly = true;
                i--;
                continue;
            }

            var value = i + 1 < args.Count ? args[i + 1] : null;

            var accepted = args[i] switch
            {
                "--runs" => TryNumber(value, 1, MaximumRuns, out runs),
                "--threads" when route.TakesWalkTuning() =>
                    TryNumber(value, WalkTuning.MinimumThreads, WalkTuning.MaximumThreads, out threads),
                "--listing-buffer" when route.TakesWalkTuning() => TryNumber(
                    value, WalkTuning.MinimumListingBuffer / 1024, WalkTuning.MaximumListingBuffer / 1024, out bufferKiB),
                "--read-size" when route.TakesTableTuning() => TryNumber(
                    value, TableTuning.MinimumReadBytes / 1024, TableTuning.MaximumReadBytes / 1024, out readKiB),
                "--reads-in-flight" when route.TakesTableTuning() => TryNumber(
                    value, TableTuning.MinimumReadsInFlight, TableTuning.MaximumReadsInFlight, out readsInFlight),
                "--parse-threads" when route.TakesTableTuning() => TryNumber(
                    value, TableTuning.MinimumParseThreads, TableTuning.MaximumParseThreads, out parseThreads),
                "--list-reads-in-flight" when route.TakesTableTuning() => TryNumber(
                    value, TableTuning.MinimumListReadsInFlight, TableTuning.MaximumListReadsInFlight, out listReadsInFlight),
                "--snapshots" when route is Route.ExploreWalk => TryOnOff(value, out snapshots),
                "--read-by" when route is Route.Links => TryReadBy(value, out readBy),
                "--files" when route is Route.Remove => TryNumber(value, 0, TreeShape.MaximumFiles, out files),
                "--depth" when route is Route.Remove => TryNumber(value, 0, TreeShape.MaximumDepth, out depth),
                "--fanout" when route is Route.Remove => TryNumber(value, 1, TreeShape.MaximumFanout, out fanout),
                "--trees" when route is Route.Clean => TryNumber(value, 1, MaximumTrees, out trees),
                "--files-per-tree" when route is Route.Clean => TryNumber(value, 0, MaximumFilesPerTree, out filesPerTree),
                "--folders" when route is Route.Protect => TryNumber(value, 1, MaximumTrees, out trees),
                "--listed" when route is Route.Protect => TryOnOff(value, out listed),
                "--build" when route is Route.Race => TryDrive(value, out build),
                "--racer" when route is Route.Race => TryRacer(value, out racer),
                "--before-part" when route is Route.Race => TryCommand(value, out beforePart),
                "--build-priority" when route is Route.Race => TryYield(value, out buildYields),
                "--checksum" when route is Route.Duplicates => TryChecksum(value, out checksum),
                "--cache" when route is Route.Duplicates => TryCache(value, out warmCache),
                _ => false,
            };

            if (!accepted)
            {
                error =
                    $"--runs takes a number from 1 to {MaximumRuns}. For walk, measure, explorewalk, sources and race, --threads takes a number from " +
                    $"{WalkTuning.MinimumThreads} to {WalkTuning.MaximumThreads} and --listing-buffer from " +
                    $"{WalkTuning.MinimumListingBuffer / 1024} to {WalkTuning.MaximumListingBuffer / 1024}. " +
                    $"For the table routes, --read-size takes a number from " +
                    $"{TableTuning.MinimumReadBytes / 1024} to {TableTuning.MaximumReadBytes / 1024}, " +
                    $"--reads-in-flight a number from {TableTuning.MinimumReadsInFlight} to " +
                    $"{TableTuning.MaximumReadsInFlight}, --parse-threads from " +
                    $"{TableTuning.MinimumParseThreads} to {TableTuning.MaximumParseThreads} and --list-reads-in-flight from " +
                    $"{TableTuning.MinimumListReadsInFlight} to {TableTuning.MaximumListReadsInFlight}. " +
                    "For the Explore walk, --snapshots takes on or off. For links, --read-by takes name or handle. " +
                    $"For remove, --files takes a number from 0 to {TreeShape.MaximumFiles}, --depth from 0 to " +
                    $"{TreeShape.MaximumDepth} and --fanout from 1 to {TreeShape.MaximumFanout}, and --read-only takes nothing. " +
                    $"For clean, --trees takes a number from 1 to {MaximumTrees} and --files-per-tree from 0 to {MaximumFilesPerTree}. " +
                    $"For protect, --folders takes a number from 1 to {MaximumTrees} and --listed takes on or off. " +
                    "For race, --build takes a drive letter, --racer takes sources or walk, --before-part a command, " +
                    "and --build-priority yield or normal. " +
                    $"For duplicates, --checksum takes {string.Join(", ", ChecksumAlgorithms.Offered.Select(Token))} and --cache cold or warm. " +
                    "Nothing else is accepted after the target.";
                return null;
            }
        }

        if (options < 2)
        {
            error = "Name a folder before the options.";
            return null;
        }

        if (TreeShape.FoldersBelowRoot(depth, fanout) is null)
        {
            error = $"A tree of that depth and fanout would have more than {TreeShape.MaximumFolders} folders.";
            return null;
        }

        if ((long)trees * filesPerTree > TreeShape.MaximumFiles)
        {
            error = $"The trees would hold more than {TreeShape.MaximumFiles} files in all.";
            return null;
        }

        var path = route.TargetsVolume() ? VolumeRoot(args[1]) : Folder(args[1]);
        string?[] folders = route.TakesFolders() ? [.. args.Take(options).Skip(1).Select(Folder)] : [path];
        if (path is null || folders.Contains(null) || (route.ReadsTable() && !VolumePath.TryParse(path, out _)))
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
            new TableTuning(readKiB * 1024, readsInFlight, parseThreads, listReadsInFlight),
            snapshots,
            readBy,
            [.. folders.Select(folder => folder!)],
            route is Route.Clean ? CleanRoute.Shape(filesPerTree) : new TreeShape(files, depth, fanout, readOnly),
            trees,
            build ?? path[0],
            racer,
            beforePart,
            buildYields,
            listed,
            checksum,
            warmCache);
    }

    /// <summary>
    /// What a scan walks the race's folder and builds its volume's index with, for the kinds of the
    /// two drives under the shipped settings, or null where the arguments name no folder or drive the
    /// race can use, which the parse then refuses with the reason.
    /// </summary>
    private static (int Threads, int BufferKiB, int ReadKiB, int ReadsInFlight, int ParseThreads, int ListReadsInFlight)? RaceDefaults(
        IReadOnlyList<string> args,
        ScanTuner tuner)
    {
        var folder = Folder(args[1]);
        var build = folder?[0];

        for (var i = 2; i + 1 < args.Count; i++)
        {
            if (args[i] is "--build" && TryDrive(args[i + 1], out var named))
            {
                build = named;
            }
        }

        if (folder is null || !VolumePath.TryParse(folder, out _) || build is not { } drive)
        {
            return null;
        }

        var walk = tuner.For(folder).Walk;
        var table = tuner.ForVolume(drive).Table;

        return (walk.Threads, walk.ListingBufferBytes / 1024, table.ReadBytes / 1024, table.ReadsInFlight, table.ParseThreads, table.ListReadsInFlight);
    }

    private static bool TryDrive(string? text, out char? drive)
    {
        drive = text is null ? null : VolumeRoot(text)?[0];
        return drive is not null;
    }

    private static bool TryCommand(string? text, out string? command)
    {
        command = string.IsNullOrWhiteSpace(text) ? null : text;
        return command is not null;
    }

    private static bool TryYield(string? text, out bool yields)
    {
        yields = text is not "normal";
        return text is "yield" or "normal";
    }

    private static bool TryRacer(string? text, out Route racer)
    {
        racer = text is "walk" ? Route.Walk : Route.Sources;
        return text is "sources" or "walk";
    }

    /// <summary>The index of the first argument after the route that is an option, or the count where none is.</summary>
    private static int FirstOption(IReadOnlyList<string> args)
    {
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return args.Count;
    }

    /// <summary>
    /// What a scan reads the table with on a drive of unknown kind: the read size and reads in flight
    /// from before they could be set, the parse threads Auto gives the machine, and the list reads in
    /// flight Auto gives every kind.
    /// </summary>
    public static TableTuning UnknownDrive { get; } =
        VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Unknown).Table;

    private static bool TryNumber(string? text, int minimum, int maximum, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= minimum
        && number <= maximum;

    private static bool TryNumber(string? text, long minimum, long maximum, out long number) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= minimum
        && number <= maximum;

    /// <summary>
    /// A checksum this machine offers, by the name other tools print it under, in any case and with or
    /// without its hyphen: <c>sha256</c>, <c>SHA-256</c> and <c>sha-256</c> are one.
    /// </summary>
    private static bool TryChecksum(string? text, out ChecksumAlgorithm algorithm)
    {
        var named = text is null
            ? null
            : ChecksumAlgorithms.Offered
                .Where(offered => string.Equals(Unhyphenated(offered.Name()), Unhyphenated(text), StringComparison.OrdinalIgnoreCase))
                .Cast<ChecksumAlgorithm?>()
                .SingleOrDefault();

        algorithm = named ?? ChecksumAlgorithm.XxHash128;
        return named is not null;
    }

    /// <summary>The name a checksum is given on the command line, as other tools print it, in lower case.</summary>
    private static string Token(ChecksumAlgorithm algorithm) => algorithm.Name().ToLowerInvariant();

    private static string Unhyphenated(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal);

    private static bool TryCache(string? text, out bool warm)
    {
        warm = text is "warm";
        return text is "cold" or "warm";
    }

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
