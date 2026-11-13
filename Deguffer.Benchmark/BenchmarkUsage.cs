namespace Deguffer.Benchmark;

/// <summary>
/// What the command line accepts, as it is printed beside a request that could not be read. Apart
/// from <see cref="BenchmarkRequest"/>, which reads the command line, so each route's help is found
/// in one place however the reading of it changes.
/// </summary>
internal static class BenchmarkUsage
{
    public const string Text =
        """
        Usage: Deguffer.Benchmark <route> <target> [--runs N] [options]
               Deguffer.Benchmark folders <folder> [<folder>...] [--runs N]
               Deguffer.Benchmark remove|clean|protect <new folder> [--runs N] [options]
               Deguffer.Benchmark race <folder> [--build <drive>] [--runs N] [options]
               Deguffer.Benchmark plan [--runs N]

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
          plan              Plan every provider as a Storage scan does, and time the whole
                            pass, when each provider took a slot and ended, and the costly
                            questions it asked Windows. The first run
                            is a fresh process, the later runs warm. It only plans: nothing
                            is cleaned. Run it elevated and not to compare the two. Write
                            its output outside the source tree, as every report.

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
}
