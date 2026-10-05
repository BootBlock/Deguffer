using System.Diagnostics;
using System.Text;

namespace Deguffer.Core.Safety;

/// <param name="ExitCode">The process exit code, or -1 if it could not be started.</param>
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
/// Runs a tool's own eviction command (§5.1). Behind an interface so a plan's command steps can
/// be asserted in tests without a package manager being installed.
/// </summary>
public interface IProcessRunner
{
    Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct);
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
public sealed class ProcessRunner(string workingDirectory) : IProcessRunner
{
    public static readonly ProcessRunner Default = new(WorkingDirectoryFor(UserEnvironment.Current));

    internal static string WorkingDirectoryFor(IUserEnvironment environment) =>
        Path.Combine(environment.LocalAppData, "Deguffer", "tool-working-directory");

    /// <summary>The directory every tool this runner starts runs in.</summary>
    public string WorkingDirectory => workingDirectory;

    public async Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            // Made on every run rather than once, so a folder removed while Deguffer is open is
            // there again for the next tool instead of failing every launch after it.
            Directory.CreateDirectory(LongPath.Extended(workingDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Running in Deguffer's own directory instead would reintroduce the wrong-folder
            // answer this directory exists to prevent, so the step fails and says why.
            return new CommandOutcome(-1, string.Empty, ex.Message);
        }

        // npm and friends ship as .cmd shims on Windows, and CreateProcess cannot launch a batch
        // file directly — it has to go through the interpreter.
        var isBatch = Path.GetExtension(fileName) is ".cmd" or ".bat";

        var startInfo = isBatch
            ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{fileName}\" {arguments}\"")
            : new ProcessStartInfo(fileName, arguments);

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            // The tool is on PATH but could not be launched — a broken shim, a missing
            // interpreter, a blocked executable. Report it as a failed step rather than taking
            // the whole run down. Deliberately not a blanket catch: cancellation and
            // out-of-memory must keep propagating.
            return new CommandOutcome(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
