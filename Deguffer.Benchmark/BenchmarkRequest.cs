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
/// fully qualified. For <see cref="Route.Folders"/>, the first of <paramref name="Folders"/>. Never
/// printed: see <see cref="MeasuredPlace"/>.
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
/// Every folder the folders route scans, fully qualified, in the order given. For any other route,
/// <paramref name="Path"/> alone.
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
    bool Listed)
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

    public const string Usage =
        """
        Usage: Deguffer.Benchmark <route> <target> [--runs N] [options]
               Deguffer.Benchmark folders <folder> [<folder>...] [--runs N]
               Deguffer.Benchmark remove|clean|protect <new folder> [--runs N] [options]
               Deguffer.Benchmark race <folder> [--build <drive>] [--runs N] [options]

        Routes:
          table <drive>     Read the volume's file table end to end, parse only. Elevated.
          index <drive>     Build the file-table index the deletion path measures from. Elevated.
          explore <drive>   Build the Explore tree for the volume from its file table. Elevated.
          explorefolder <folder>
                            Build the Explore tree for the folder from its volume's file
                            table: the table's side of an elevated Explore of a folder,
                            which races it against a walk under Auto. Elevated.
          walk <folder>     Walk the folder as an unelevated scan does.
          measure <folder>  Measure the folder as an unelevated scan does: the walk, and
                            what the scan adds up for each entry.
          explorewalk <folder>
                            Build the Explore tree for the folder by walking it, as an
                            unelevated Explore does.
          sources <folder>  Find build outputs in the folder as an unelevated scan does.
          links <folder>    Measure the folder as a hard-linked store such as pnpm's is
                            measured, reading each file's link count and sizes.
          folders <folder> [<folder>...]
                            Scan the folders as a duplicate search does: each volume's file
                            table read once where it can be, the walk where it cannot, and
                            the volumes on separate disks at the same time. Timed against
                            each folder alone, the difference is what that saves.
          remove <new folder>
                            Create the folder, build a tree in it before each run, and
                            remove the tree as a clean does: the walk, the file pass and
                            the folder pass, timed apart.
          clean <new folder>
                            Create the folder, build many small build folders in it before
                            each run, and remove them as one plan, as a clean of the obj
                            row does: what each step costs around its removal.
          protect <new folder>
                            Create the folder, build many empty folders in it once, and
                            record each as a plan records the paths it protects: whether
                            each is there and holds anything, several asked at once.
          race <folder>     Walk the folder while the index of its volume is built, both
                            started at the same moment, as Auto races them in a scan, and
                            each of the two alone, alternating the order every round. The
                            slowdown of each is what the other costs it. Elevated.
          scan <folder>     Measure the folder as an elevated scan does the first time it
                            asks about the volume: open it, find its table, and race the
                            walk against the build, after an untimed walk. Elevated.

        --runs N               How many times to run the route, 1 to 1000. The default is 5. The
                               first run is reported apart from the others.
        --threads N            Walk, measure, explorewalk, sources and race only. How many
                               folders are listed at once, 1 to 64. The default is what a scan
                               uses.
        --listing-buffer KiB   Walk, measure, explorewalk, sources and race only. How many KiB of
                               entries each listing asks Windows for, 4 to 1024. The default
                               is what a scan uses.
        --read-by name|handle  Links only. Whether each file is read by name, as a scan does
                               where the file system answers that, or through a handle opened
                               on it. The default is name.
        --snapshots on|off     Explore walk only. Whether it assembles the pictures of the tree
                               so far that Explore draws while it walks. The default is on.
        --files N              Remove only. How many empty files the tree holds, 0 to 10000000.
                               The default is 100000.
        --depth N              Remove only. How many levels of folders the tree has below its
                               root, 0 to 200. The default is 5.
        --fanout N             Remove only. How many folders each folder above the deepest
                               holds, 1 to 1000, for at most 1000000 folders. The default is 4.
        --read-only            Remove only. Mark every file read-only.
        --trees N              Clean only. How many build folders the plan removes, 1 to
                               100000. The default is 500.
        --files-per-tree N     Clean only. How many empty files each build folder holds, in
                               seven folders, 0 to 100000, for at most 10000000 files in all.
                               The default is 100.
        --folders N            Protect only. How many empty folders are built and recorded, 1
                               to 100000. The default is 5000.
        --listed on|off        Protect only. Whether the folders are recorded from one listing
                               of the folder that holds them, as a row that listed them while
                               it planned does, rather than each asked of the disk. The listing
                               is not timed. The default is off.
        --read-size KiB        Table routes only. How many KiB of records each read asks for, 4
                               to 16384.
        --reads-in-flight N    Table routes only. How many reads are outstanding at once, 1 to 32.
        --parse-threads N      Table routes only. How many threads parse records, 1 to 64.
        --list-reads-in-flight N
                               Table routes only. How many reads of the attribute lists kept
                               outside the table are outstanding at once, 1 to 32.
        --build <drive>        Race only. The volume whose index is built beside the walk.
                               The default is the folder's own.
        --racer sources|walk   Race only. Whether the folder is searched for build outputs, as
                               discovery searches it, or walked whole. The default is sources.
        --before-part <command>
                               Race only. A command run through cmd.exe before each part and
                               outside its time, such as one that empties the system file
                               cache so every part lists from the drive. A command that fails
                               stops the run. Deguffer never empties the cache itself.
        --build-priority yield|normal
                               Race only. Whether the build reads at low priority while the
                               walk runs, as Auto's does where the two share a disk, or at
                               normal priority throughout. The default is yield.

        A table route's defaults are what a scan uses on a drive of unknown kind, except the
        race's: it walks and builds with what a scan uses for each drive's kind, under the
        shipped settings, as the race it reproduces does. The Explore routes also report what
        the finished tree keeps on the managed heap.

        Every route but remove, clean and protect only reads: nothing on the drive is written,
        moved or deleted. Those three refuse a folder that already exists, create it, write and
        delete only inside it, and remove it when they finish.
        """;

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
            error = "The route must be table, index, explore, explorefolder, walk, measure, explorewalk, sources, links, folders, remove, clean, protect, race or scan.";
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

        // Before the options, because the race's defaults depend on the drives it walks and builds
        // on, and a value an option sets has to win over them.
        if (route is Route.Race && RaceDefaults(args, tuner) is { } race)
        {
            (threads, bufferKiB, readKiB, readsInFlight, parseThreads, listReadsInFlight) = race;
        }

        // Only the folders route takes more than one target, so for it the options start at the
        // first argument that is one.
        var options = route is Route.Folders ? FirstOption(args) : 2;

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
        string?[] folders = route is Route.Folders ? [.. args.Take(options).Skip(1).Select(Folder)] : [path];
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
            listed);
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
