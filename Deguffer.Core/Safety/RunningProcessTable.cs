using System.Collections.Concurrent;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Safety;

/// <param name="Id">
/// The operating system's own identifier for it, which is the only thing that tells one process
/// from another. An image path does not: several processes run the same executable, and under a
/// shared host that executable is not even the application's.
/// </param>
/// <param name="Name">The process name, for telling the user what to close.</param>
/// <param name="ImagePath">
/// Where its executable lives, or null where that could not be read or put in a form that compares.
/// </param>
/// <param name="CurrentDirectory">Its working directory, or null where that could not be read.</param>
/// <param name="PathArguments">
/// The full paths it was started with as arguments, empty where it was given none or its command
/// line could not be read. See <see cref="CommandLinePaths"/> for which arguments count.
/// </param>
/// <remarks>
/// Every path here is in <see cref="LongPath.Canonical"/> form, so a caller compares them as they
/// stand. One that could not be put in it is left out, and the table says so.
/// </remarks>
internal sealed record RunningProcess(
    int Id,
    string Name,
    string? ImagePath,
    string? CurrentDirectory,
    IReadOnlyList<string> PathArguments)
{
    /// <summary>
    /// When the kernel created it, or null where that could not be read. Only a query that names
    /// <see cref="LiveTreeQuery.Writers"/> asks it, so a null here leaves that query unanswered
    /// rather than the whole table.
    /// </summary>
    public DateTimeOffset? Started { get; init; }
}

/// <param name="Processes">Every process this account was allowed to look at.</param>
/// <param name="ImagePathsReadable">
/// Whether every executable path that was read could be put in a form that compares. False means
/// one could not, so its <see cref="RunningProcess.ImagePath"/> is null although the program runs
/// from somewhere, and a null image path is not evidence that nothing runs from a directory.
/// </param>
/// <param name="CurrentDirectoriesReadable">
/// Whether every working directory that was read can be trusted. False means the layout self-check
/// failed, so every <see cref="RunningProcess.CurrentDirectory"/> is null because nothing could be
/// read, or a 32-bit process's block could not be checked, or a directory that was read could not
/// be put in a form that compares. In the last two its directory is null although it has one.
/// Either way a null directory is not evidence that nothing is working there.
/// </param>
/// <param name="CommandLinesReadable">
/// Whether every path a process was started with is in <see cref="RunningProcess.PathArguments"/>.
/// False means this process could not read its own command line, so every list is empty because
/// nothing could be read, or a path in one could not be put in a form that compares and was left
/// out. Either way an empty list is not evidence that no process was started with a path.
/// </param>
internal sealed record ProcessTable(
    IReadOnlyList<RunningProcess> Processes,
    bool ImagePathsReadable,
    bool CurrentDirectoriesReadable,
    bool CommandLinesReadable);

/// <summary>
/// One pass over the process table, answering the three questions that can be asked about a directory
/// rather than about a file.
///
/// <list type="bullet">
/// <item>Is a running program's <b>executable</b> inside it? That is a <c>.venv</c> whose interpreter
/// is running, or a binary started from <c>target\debug</c>.</item>
/// <item>Is a running program's <b>working directory</b> inside the project? That is a build in
/// flight, a shell sitting in the project, or an editor with the solution open — Visual Studio's
/// working directory is the solution's own folder, observed rather than assumed.</item>
/// <item>Was a running program <b>started with a path</b> inside it? That is a test browser using
/// a profile in <c>%TEMP%</c>, which it neither runs from nor works in: Playwright passes the
/// profile as <c>--user-data-dir=</c> to Chromium and <c>-profile</c> to Firefox, observed on a
/// real run.</item>
/// </list>
///
/// <para>All three are readable without elevation for every process this account owns. One pass
/// over roughly five hundred processes cost about thirty milliseconds for the first two, measured
/// on the machine <c>docs/todo/unreached-locations.md</c> §2 was written against. Reading command
/// lines as well took a pass over 332 processes from about 27 to about 55 milliseconds on another
/// machine. It is read in the same pass because the test browser profiles row asks it on every
/// preview, and a second pass would open every process again.</para>
///
/// <para><b>The working directory has no documented accessor</b>, so it is read out of the process
/// environment block at an offset Windows does not promise to keep. A layout that moved would produce
/// nonsense matching no directory, which reads as "nothing is using this" — the one wrong answer
/// that costs somebody their work. So the offset is checked against this process, whose own
/// working directory is already known, and a mismatch turns the mechanism off and says so rather
/// than quietly reporting an empty result. A 32-bit process keeps its directory in a second block,
/// which <see cref="ProcessWorkingDirectory"/> checks per process instead.</para>
/// </summary>
internal static class RunningProcessTable
{
    /// <summary>
    /// How many processes are read at once.
    ///
    /// <para>Each read is a handful of kernel calls (an open, a memory read, an image path and a
    /// command line), and one after another a table of 774 processes took 65 to 99 ms on one machine,
    /// and 151 to 219 ms inside a planning pass. The calls wait on the kernel rather than on one
    /// disk, so the bound is the processors, never more than eight, because past that the
    /// table is a few milliseconds and more threads only contend with the rows planning beside it.</para>
    /// </summary>
    internal static readonly int ProcessesAtOnce = Math.Clamp(Environment.ProcessorCount, 1, 8);

    public static ProcessTable Read(IProcessTableCalls calls, CancellationToken ct = default) =>
        Read(calls, ProcessesAtOnce, ct);

    /// <param name="threads">How many processes are read at once: the seam a test holds two opens in flight through.</param>
    internal static ProcessTable Read(IProcessTableCalls calls, int threads, CancellationToken ct)
    {
        using var timing = MachineCallTally.Time(MachineCall.ProcessTable);

        var readable = LayoutIsSound(calls);
        var commandLines = CommandLinesAreReadable(calls);
        var listed = calls.List();

        // One program is many processes started with the same paths, and a path carrying an alias
        // costs a walk up the disk to put in canonical form, so each spelling is put in it once.
        // Two threads may both make one spelling canonical, which costs a walk and changes nothing.
        var known = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        Func<string, string?> canonical = path => known.GetOrAdd(path, LongPath.Canonical);

        // Each process's answer goes in its own place, so the table lists them in the order the system
        // did, whatever order they were read in.
        var read = new ProcessReading[listed.Count];

        ListWorkers.Run(
            listed.Count,
            threads,
            index => read[index] = ReadOne(calls, listed[index], readable, commandLines, canonical),
            ct);

        var verified = readable;
        var imagesWhole = true;
        var argumentsWhole = true;
        var processes = new List<RunningProcess>(read.Length);

        foreach (var reading in read)
        {
            if (reading.Process is not { } process)
            {
                continue;
            }

            verified &= reading.DirectoryWhole;
            imagesWhole &= reading.ImageWhole;
            argumentsWhole &= reading.ArgumentsWhole;
            processes.Add(process);
        }

        return new ProcessTable(processes, imagesWhole, verified, commandLines && argumentsWhole);
    }

    /// <summary>
    /// One process as it was read, and whether each of its paths that was read could be put in a form
    /// that compares. <see cref="Process"/> is null where it could not be opened.
    /// </summary>
    private readonly record struct ProcessReading(
        RunningProcess? Process,
        bool DirectoryWhole,
        bool ImageWhole,
        bool ArgumentsWhole);

    private static ProcessReading ReadOne(
        IProcessTableCalls calls,
        ListedProcess listed,
        bool readable,
        bool commandLines,
        Func<string, string?> canonical)
    {
        using var process = calls.Open(listed.Id, withMemory: readable);

        if (process is null)
        {
            // A process of another account, or a protected one. Neither is the developer's
            // own editor or build, which is the only thing this is looking for.
            return default;
        }

        var directory = readable && process.Memory is { } memory
            ? ProcessWorkingDirectory.Of(memory)
            : WorkingDirectoryRead.Unread;

        var working = directory.Directory is { } read ? canonical(read) : null;
        var imageRead = process.ImagePath();
        var image = imageRead is { } path ? canonical(path) : null;
        var argumentsWhole = true;

        var running = new RunningProcess(
            listed.Id,
            listed.Name,
            image,
            working,
            commandLines && process.CommandLine() is { } line
                ? CanonicalArguments(line, canonical, ref argumentsWhole)
                : [])
        {
            Started = process.StartTime(),
        };

        // Only this process's directory is in doubt, so the others are still read.
        return new ProcessReading(
            running,
            DirectoryWhole: !directory.LayoutUnverified && (directory.Directory is null || working is not null),
            ImageWhole: imageRead is null || image is not null,
            ArgumentsWhole: argumentsWhole);
    }

    /// <summary>
    /// The paths <paramref name="line"/> names, each canonical, so that every spelling of one folder
    /// compares as that folder. One that cannot be made canonical clears <paramref name="whole"/>
    /// rather than being compared as it arrived, where it would match nothing and read as unused.
    /// </summary>
    private static IReadOnlyList<string> CanonicalArguments(
        string line,
        Func<string, string?> canonicalOf,
        ref bool whole)
    {
        var paths = new List<string>();

        foreach (var argument in CommandLinePaths.Of(line))
        {
            if (canonicalOf(argument) is { } canonical)
            {
                paths.Add(canonical);

                // The program opens the argument through Win32, which may read it as another folder.
                if (LongPath.CanonicalAsOpened(argument) is { } opened)
                {
                    paths.Add(opened);
                }
            }
            else
            {
                whole = false;
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether the environment-block offset still describes this Windows, checked against the one
    /// process whose working directory is already known.
    /// </summary>
    private static bool LayoutIsSound(IProcessTableCalls calls)
    {
        using var own = calls.Open(Environment.ProcessId, withMemory: true);

        if (own?.Memory is not { } memory || ProcessWorkingDirectory.Of(memory).Directory is not { } read)
        {
            return false;
        }

        // Windows stores it with a trailing separator; Environment does not.
        return Path.TrimEndingDirectorySeparator(read).Equals(
            Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether this Windows answers <c>ProcessCommandLineInformation</c> at all, asked of this
    /// process. It was added in Windows 8.1, and where it is refused every process would otherwise
    /// read as having been started with no paths.
    /// </summary>
    private static bool CommandLinesAreReadable(IProcessTableCalls calls)
    {
        using var own = calls.Open(Environment.ProcessId, withMemory: false);

        return own?.CommandLine() is not null;
    }
}
