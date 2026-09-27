namespace Deguffer.Core.Safety;

/// <param name="Id">
/// The operating system's own identifier for it, which is the only thing that tells one process
/// from another. An image path does not: several processes run the same executable, and under a
/// shared host that executable is not even the application's.
/// </param>
/// <param name="Name">The process name, for telling the user what to close.</param>
/// <param name="ImagePath">Where its executable lives, or null where that could not be read.</param>
/// <param name="CurrentDirectory">Its working directory, or null where that could not be read.</param>
/// <param name="PathArguments">
/// The full paths it was started with as arguments, empty where it was given none or its command
/// line could not be read. See <see cref="CommandLinePaths"/> for which arguments count.
/// </param>
internal sealed record RunningProcess(
    int Id,
    string Name,
    string? ImagePath,
    string? CurrentDirectory,
    IReadOnlyList<string> PathArguments);

/// <param name="Processes">Every process this account was allowed to look at.</param>
/// <param name="CurrentDirectoriesReadable">
/// Whether every working directory that was read can be trusted. False means either the layout
/// self-check failed, so every <see cref="RunningProcess.CurrentDirectory"/> is null because nothing
/// could be read, or a 32-bit process's block could not be checked, so its directory is null
/// although it has one. Either way a null directory is not evidence that nothing is working there.
/// </param>
/// <param name="CommandLinesReadable">
/// Whether command lines could be read at all. False means this process could not read its own, so
/// every <see cref="RunningProcess.PathArguments"/> is empty because nothing could be read rather
/// than because no process was started with a path.
/// </param>
internal sealed record ProcessTable(
    IReadOnlyList<RunningProcess> Processes,
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
    public static ProcessTable Read(IProcessTableCalls calls, CancellationToken ct = default)
    {
        var readable = LayoutIsSound(calls);
        var verified = readable;
        var commandLines = CommandLinesAreReadable(calls);
        var processes = new List<RunningProcess>();

        foreach (var (id, name) in calls.List())
        {
            ct.ThrowIfCancellationRequested();

            using var process = calls.Open(id, withMemory: readable);

            if (process is null)
            {
                // A process of another account, or a protected one. Neither is the developer's
                // own editor or build, which is the only thing this is looking for.
                continue;
            }

            var directory = readable && process.Memory is { } memory
                ? ProcessWorkingDirectory.Of(memory)
                : WorkingDirectoryRead.Unread;

            // Only this process's directory is in doubt, so the others are still read.
            if (directory.LayoutUnverified)
            {
                verified = false;
            }

            processes.Add(new RunningProcess(
                id,
                name,
                LongPath.Unaliased(process.ImagePath()),
                LongPath.Unaliased(directory.Directory),
                commandLines && process.CommandLine() is { } line
                    ? [.. CommandLinePaths.Of(line).Select(path => LongPath.Unaliased(path))]
                    : []));
        }

        return new ProcessTable(processes, verified, commandLines);
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
