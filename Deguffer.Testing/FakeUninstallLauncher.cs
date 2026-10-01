using Deguffer.Core.InstalledApps;

namespace Deguffer.Testing;

/// <summary>An uninstaller that does whatever a test says, and records what was started.</summary>
public sealed class FakeUninstallLauncher : IUninstallLauncher
{
    /// <summary>Every launch started, in order.</summary>
    public List<UninstallLaunch> Started { get; } = [];

    /// <summary>What the uninstaller does to the machine while it runs.</summary>
    public Action<UninstallLaunch>? WhileRunning { get; set; }

    /// <summary>The outcome it reports: started and exited, by default.</summary>
    public LaunchOutcome Outcome { get; set; } = new(true, "The uninstaller exited with code 0.");

    /// <summary>Whether the uninstaller never exits, so only a cancel ends the wait.</summary>
    public bool RunsUntilCancelled { get; set; }

    public async Task<LaunchOutcome> RunAsync(UninstallLaunch launch, CancellationToken ct)
    {
        if (Outcome.Started)
        {
            Started.Add(launch);
            WhileRunning?.Invoke(launch);

            if (RunsUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        return Outcome;
    }
}
