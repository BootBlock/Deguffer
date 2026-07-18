using System.Diagnostics;
using System.Text;

namespace Deguffer.Core.Safety;

/// <param name="ExitCode">The process exit code, or -1 if it could not be started.</param>
public sealed record CommandOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Whatever the tool said, preferring stdout — for surfacing in the result.</summary>
    public string Message =>
        !string.IsNullOrWhiteSpace(StandardOutput) ? StandardOutput.Trim()
        : !string.IsNullOrWhiteSpace(StandardError) ? StandardError.Trim()
        : $"exit code {ExitCode}";
}

/// <summary>
/// Runs a tool's own eviction command (§5.1). Behind an interface so a plan's command steps can
/// be asserted in tests without a package manager being installed.
/// </summary>
public interface IProcessRunner
{
    Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Default = new();

    public async Task<CommandOutcome> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
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

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandOutcome(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
