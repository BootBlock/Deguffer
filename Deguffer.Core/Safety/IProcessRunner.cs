using System.ComponentModel;

namespace Deguffer.Core.Safety;

/// <param name="ExitCode">
/// The process exit code, or -1 if it could not be started or was ended at its <see cref="ToolRun.Limit"/>.
/// </param>
public sealed record CommandOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Whatever the tool said, for surfacing in the result: stderr first where the command failed,
    /// and stdout first where it did not, each falling back to the other.
    ///
    /// <para>A failed command's reason is on stderr, and its stdout is often a progress line written
    /// before it failed. <c>Delete-DeliveryOptimizationCache</c> writes "Deleting..." and then throws,
    /// so preferring stdout reported the progress line and hid why nothing was cleared.</para>
    /// </summary>
    public string Message
    {
        get
        {
            var (first, second) = Succeeded ? (StandardOutput, StandardError) : (StandardError, StandardOutput);

            return !string.IsNullOrWhiteSpace(first) ? first.Trim()
                : !string.IsNullOrWhiteSpace(second) ? second.Trim()
                : $"exit code {ExitCode}";
        }
    }
}

/// <summary>
/// Runs a tool's own eviction command (§5.1), or a query of what a tool knows. Behind an interface so
/// a plan's command steps can be asserted in tests without a package manager being installed.
/// </summary>
public interface IProcessRunner
{
    /// <param name="run">
    /// How long the tool may run, and whether a cancellation ends it. See <see cref="ToolRun"/>.
    /// </param>
    /// <param name="ct">
    /// Never starts a tool once cancelled. A started tool that is <see cref="CancelledCommand.Ended"/>
    /// is ended with everything it started, and the cancellation is thrown once they have exited, so
    /// nothing it started is still at work when the caller moves on. One that
    /// <see cref="CancelledCommand.Finishes"/> is waited for, and its outcome returned.
    /// </param>
    Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct);
}

/// <summary>
/// Runs every tool in one folder Deguffer owns, never in Deguffer's own working directory.
///
/// <para>pnpm chooses its store from the drive of the directory it runs in, and NuGet, npm, uv,
/// Poetry and PlatformIO read project configuration from that directory and its parents. Deguffer's
/// own directory is whatever the shortcut or the terminal that started it said, so a row described
/// as the profile's cache would measure, clean and protect a project's or another drive's instead,
/// all three agreeing on the wrong folder. The folder is fixed, holds nothing a tool reads as
/// configuration, and is on the drive of the profile's caches.</para>
///
/// <para>Not a parameter of <see cref="IProcessRunner.RunAsync"/>: every command names its targets
/// by absolute path, and no caller has a reason to choose a different directory, so there is no
/// choice for a caller to get wrong.</para>
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Default = new(WorkingDirectoryFor(UserEnvironment.Current));

    private readonly string _workingDirectory;
    private readonly Func<ToolJob> _lifetime;

    public ProcessRunner(string workingDirectory)
        : this(workingDirectory, () => ToolJob.Lifetime)
    {
    }

    /// <param name="lifetime">
    /// The job every tool that may be ended is placed in, which ends what is left in it when it closes.
    /// The shared one lasts as long as Deguffer, so a test gives its own to prove what closing it does.
    /// </param>
    internal ProcessRunner(string workingDirectory, Func<ToolJob> lifetime)
    {
        _workingDirectory = workingDirectory;
        _lifetime = lifetime;
    }

    internal static string WorkingDirectoryFor(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "Deguffer", "tool-working-directory");

    /// <summary>The directory every tool this runner starts runs in.</summary>
    internal string WorkingDirectory => _workingDirectory;

    public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Windows gives an empty LocalAppData where no profile is loaded, and the folder composed
        // from it is relative, so it would resolve against Deguffer's own directory: the folder
        // this runner exists to keep tools out of.
        if (!Path.IsPathFullyQualified(_workingDirectory))
        {
            return new CommandOutcome(
                -1, string.Empty, $"Not run, because the folder tools run in, '{_workingDirectory}', is not a full path.");
        }

        // Started by full path only, so Windows searches nowhere for the program, Deguffer's own
        // directory included. Every caller resolves its tool before asking.
        if (!Path.IsPathFullyQualified(fileName))
        {
            return new CommandOutcome(-1, string.Empty, $"Not run, because '{fileName}' is not a full path.");
        }

        try
        {
            // Made on every run rather than once, so a folder removed while Deguffer is open is
            // there again for the next tool instead of failing every launch after it.
            Directory.CreateDirectory(LongPath.Extended(_workingDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Running in Deguffer's own directory instead would reintroduce the wrong-folder
            // answer this directory exists to prevent, so the step fails and says why.
            return new CommandOutcome(-1, string.Empty, ex.Message);
        }

        var (application, commandLine) = CommandLineFor(fileName, arguments);

        ToolJob? job = null;
        ToolProcess tool;

        try
        {
            // A job of its own as well as the lifetime one, so ending this tool ends what it started
            // and nothing another run started.
            IReadOnlyList<ToolJob> jobs = run.WhenCancelled is CancelledCommand.Ended
                ? [_lifetime(), job = ToolJob.Create(killOnClose: false)]
                : [];

            // The plain form of the folder, because cmd.exe refuses an extended-length current
            // directory, and the .cmd shims run through it. The folder is a short child of LocalAppData.
            tool = ToolProcess.Start(application, commandLine, _workingDirectory, jobs);
        }
        catch (Win32Exception ex)
        {
            // The tool is on PATH but could not be launched — a broken shim, a missing
            // interpreter, a blocked executable — or Windows would not make its job. Report it as a
            // failed step rather than taking the whole run down. Deliberately not a blanket catch:
            // out-of-memory must keep propagating.
            job?.Dispose();
            return new CommandOutcome(-1, string.Empty, ex.Message);
        }

        using (job)
        using (tool)
        {
            if (job is null)
            {
                // Left to finish: the cancellation is the caller's to act on once the tool is done.
                await tool.Exited.ConfigureAwait(false);
                return new CommandOutcome(tool.ExitCode, tool.StandardOutput, tool.StandardError);
            }

            using var limit = new CancellationTokenSource(run.Limit ?? Timeout.InfiniteTimeSpan);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);

            try
            {
                await tool.Exited.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                await job.EndAsync().ConfigureAwait(false);
                await tool.EndedAsync().ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                return new CommandOutcome(
                    -1,
                    tool.StandardOutput,
                    $"{Path.GetFileName(fileName)} did not finish within {Described(run.Limit!.Value)}, so Deguffer ended it.");
            }

            return new CommandOutcome(tool.ExitCode, tool.StandardOutput, tool.StandardError);
        }
    }

    /// <summary>
    /// The program Windows starts and the command line it is given, quoted as
    /// <see cref="System.Diagnostics.Process"/> quoted it, which every caller's arguments were written for.
    /// </summary>
    private static (string Application, string CommandLine) CommandLineFor(string fileName, string arguments)
    {
        // npm and friends ship as .cmd shims on Windows, and CreateProcess cannot launch a batch
        // file directly — it has to go through the interpreter.
        if (Path.GetExtension(fileName) is ".cmd" or ".bat")
        {
            var cmd = NativeSystemTool.In(SystemDirectories.Current, "cmd.exe");
            return (cmd, $"\"{cmd}\" /d /c \"\"{fileName}\" {arguments}\"");
        }

        return (fileName, arguments.Length == 0 ? $"\"{fileName}\"" : $"\"{fileName}\" {arguments}");
    }

    private static string Described(TimeSpan limit) =>
        limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0.#} minutes" : $"{limit.TotalSeconds:0.#} seconds";
}
