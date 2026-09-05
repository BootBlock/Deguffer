using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Deguffer.Core.Execution;

namespace Deguffer.App.Shell;

/// <summary>
/// Restarts Deguffer with administrator rights so §5.5's fast path becomes reachable.
///
/// §6.3 deliberately does not elevate at startup — the tool reads the whole of the user's disk,
/// and taking administrator rights before the user has asked for anything is exactly the posture it
/// is trying not to have. Elevation is therefore something the user opts into, and nothing here
/// runs until they press the button.
///
/// That button is on screen from the start rather than only after a scan has reported what
/// elevating would buy. Reaching the elevated scan solely through the unelevated one it replaces is
/// not an opt-in, and <see cref="ElevationOffer"/> is where that judgement is made.
///
/// This relaunches unpackaged via ShellExecute, which is the only way to raise the UAC prompt: a
/// process cannot gain rights it started without.
/// </summary>
public static class ElevatedRelaunch
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// What this instance was started by <see cref="TryRelaunch"/> to do, or null for an ordinary
    /// launch. The user already asked for a scan by pressing the button; making them press it again
    /// in the new window, on a page they did not leave, is the tool forgetting what it was told.
    ///
    /// <para>Index 0 is skipped because it is the executable's own path rather than an argument.</para>
    /// </summary>
    public static ElevationRequest? Requested { get; } =
        ElevationRequest.From(Environment.GetCommandLineArgs().Skip(1));

    /// <summary>
    /// Ask for elevation and start the replacement process, telling it to pick up where this one
    /// left off. Returns false when the user dismissed the UAC prompt, which is a decision rather
    /// than a failure — the caller keeps running unelevated and says so.
    /// </summary>
    public static bool TryRelaunch(ElevationRequest request)
    {
        // ProcessPath is the host executable rather than the managed assembly, which is what
        // ShellExecute needs — starting the .dll would find no verb to run it with.
        if (Environment.ProcessPath is not { } executable)
        {
            return false;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // Through ArgumentList rather than a joined string, so a path with a space in it reaches the
        // replacement as one argument.
        foreach (var argument in request.ToArguments())
        {
            start.ArgumentList.Add(argument);
        }

        // Before the replacement exists, not after this instance closes: it reads the stored window
        // placement as it opens, so a write left to the close that follows is one it can miss.
        App.RememberWindowPlacement();

        try
        {
            Process.Start(start);

            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            // Declining the prompt is the one outcome that is expected often enough to be ordinary.
            return false;
        }
    }

    private const int ErrorCancelled = 1223;
}
