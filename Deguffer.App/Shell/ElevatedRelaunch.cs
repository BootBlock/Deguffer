using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Deguffer.App.Shell;

/// <summary>
/// Restarts Deguffer with administrator rights so §5.5's fast path becomes reachable.
///
/// §6.3 deliberately does not elevate at startup — the tool reads the whole of the user's disk,
/// and asking for administrator before the user has been shown anything is exactly the posture it
/// is trying not to have. Elevation is therefore something the user opts into, once they can see
/// what it buys them.
///
/// This relaunches unpackaged via ShellExecute, which is the only way to raise the UAC prompt: a
/// process cannot gain rights it started without.
/// </summary>
public static class ElevatedRelaunch
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Whether this instance was started by <see cref="TryRelaunch"/> and should preview
    /// immediately. The user already asked for a scan by pressing the button; making them press it
    /// again in the new window is the tool forgetting what it was told.
    /// </summary>
    public static bool ShouldRescanOnLaunch { get; } =
        Environment.GetCommandLineArgs().Contains(RescanSwitch, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ask for elevation and start the replacement process. Returns false when the user dismissed
    /// the UAC prompt, which is a decision rather than a failure — the caller keeps running
    /// unelevated and says so.
    /// </summary>
    public static bool TryRelaunch()
    {
        // ProcessPath is the host executable rather than the managed assembly, which is what
        // ShellExecute needs — starting the .dll would find no verb to run it with.
        if (Environment.ProcessPath is not { } executable)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { RescanSwitch },
            });

            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            // Declining the prompt is the one outcome that is expected often enough to be ordinary.
            return false;
        }
    }

    private const int ErrorCancelled = 1223;

    private const string RescanSwitch = "--rescan";
}
