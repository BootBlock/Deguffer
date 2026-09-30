using System.ComponentModel;
using System.Diagnostics;

namespace Deguffer.Core.InstalledApps;

/// <summary>What starting an uninstaller came to.</summary>
public sealed record LaunchOutcome(bool Started, string Message);

/// <summary>
/// Starts an uninstaller and waits for it, behind a seam so the uninstall flow is proved without
/// running one.
/// </summary>
public interface IUninstallLauncher
{
    /// <summary>
    /// Start <paramref name="launch"/> as Windows would and wait, with no deadline, until it exits.
    /// Cancelling stops the wait, never the uninstaller.
    /// </summary>
    Task<LaunchOutcome> RunAsync(UninstallLaunch launch, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// Through the shell, so an uninstaller that needs administrator rights asks for them itself:
/// <c>CreateProcess</c> fails such a program with <c>ERROR_ELEVATION_REQUIRED</c>. And with no
/// deadline, because an uninstaller's questions wait for a person (§7.3).
/// </remarks>
public sealed class ShellUninstallLauncher : IUninstallLauncher
{
    public static ShellUninstallLauncher Default { get; } = new();

    private const int ErrorCancelled = 1223;

    private ShellUninstallLauncher()
    {
    }

    public async Task<LaunchOutcome> RunAsync(UninstallLaunch launch, CancellationToken ct)
    {
        Process? process;

        try
        {
            process = Process.Start(new ProcessStartInfo(launch.FileName, launch.Arguments) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new LaunchOutcome(false, "The uninstaller did not start, because the administrator prompt was declined.");
        }
        catch (Win32Exception ex)
        {
            return new LaunchOutcome(false, $"Windows could not start the uninstaller: {ex.Message}");
        }

        if (process is null)
        {
            // The shell handed the command to a process that was already running.
            return new LaunchOutcome(true, "The uninstaller was handed to a program that was already running, so Deguffer could not watch it.");
        }

        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            try
            {
                return new LaunchOutcome(true, $"The uninstaller exited with code {process.ExitCode}.");
            }
            catch (InvalidOperationException)
            {
                // An elevated uninstaller's exit code is not always readable through the handle
                // the shell returned.
                return new LaunchOutcome(true, "The uninstaller exited.");
            }
        }
    }
}
