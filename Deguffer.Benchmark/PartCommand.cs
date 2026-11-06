using System.Diagnostics;

namespace Deguffer.Benchmark;

/// <summary>
/// A command the person running the race gives, run before each part of a round and outside its
/// time: one that empties the system file cache makes every part list from the drive, which is
/// what a cold result means.
///
/// <para>Deguffer never empties a memory list itself (§2), so the command is the person's own tool,
/// run through <c>cmd.exe</c> as they would type it. It is waited for, and a command that fails
/// stops the run, so a part is never reported as having followed a command that did not do its
/// work.</para>
/// </summary>
internal static class PartCommand
{
    /// <exception cref="PartCommandFailedException">The command could not be started, or ended with an exit code other than 0.</exception>
    public static void Run(string command)
    {
        // /d skips the AutoRun commands the registry can add to every cmd.exe, so only the command
        // given runs. /s makes cmd.exe remove the outer pair of quotes added here and nothing else,
        // so the quotes inside reach it as typed. ArgumentList would escape them with backslashes,
        // which cmd.exe does not read.
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? process;

        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PartCommandFailedException($"cmd.exe could not be started: error {ex.NativeErrorCode}.");
        }

        using (process)
        {
            if (process is null)
            {
                throw new PartCommandFailedException("cmd.exe could not be started.");
            }

            // Read to the end, both at once, so a command that writes a lot never blocks on a full
            // pipe. What it writes is dropped: it can name paths, and a result names none.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(output, errors);

            if (process.ExitCode != 0)
            {
                throw new PartCommandFailedException($"The command run before each part ended with exit code {process.ExitCode}.");
            }
        }
    }
}
