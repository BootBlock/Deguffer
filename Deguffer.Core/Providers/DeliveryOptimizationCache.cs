using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Windows says its Delivery Optimization cache holds, and the command that clears it, both
/// through Windows' own <c>DeliveryOptimization</c> PowerShell module.
///
/// <para><b>The module is the only route that answers unelevated.</b> The cache is in the Network
/// Service profile, which refuses the signed-in account a listing, and a policy can move it to
/// another drive. <c>Get-DeliveryOptimizationPerfSnap</c> reports <c>CacheSizeBytes</c> without
/// elevation and without walking anything, so the figure is Windows' own and exact.</para>
///
/// <para><b>Windows PowerShell, from the Windows directory.</b> The module ships with Windows
/// PowerShell under <c>System32</c>, so that host is named by where Windows put it rather than found
/// on <c>PATH</c>. The in-box module loads under every execution policy, including
/// <c>Restricted</c>, which was observed on Windows 11 24H2, so no policy is overridden.</para>
/// </summary>
internal sealed class DeliveryOptimizationCache : IToolMeasurement
{
    /// <summary>
    /// The eviction, and deliberately without <c>-IncludePinnedFiles</c>. A pinned file is one
    /// something told Delivery Optimization to keep, and overriding that would be Deguffer putting its
    /// own judgement in place of the service's. <c>-Force</c> answers the cmdlet's own confirmation,
    /// which otherwise waits on a key press that a process with no console can never give.
    /// </summary>
    public const string ClearArguments =
        "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"";

    /// <summary>
    /// The figure, as a string so it reaches the output in the invariant form Windows PowerShell
    /// converts numbers with, whatever the console's culture. The warning the cmdlet writes when no
    /// transfer is running is silenced, because it is not a failure.
    /// </summary>
    public const string MeasureArguments =
        "-NoProfile -NonInteractive -Command "
        + "\"[string](Get-DeliveryOptimizationPerfSnap -WarningAction SilentlyContinue).CacheSizeBytes\"";

    private readonly IProcessRunner _runner;

    public DeliveryOptimizationCache(ISystemDirectories system, IProcessRunner runner)
    {
        _runner = runner;

        var host = Path.Combine(system.WindowsDirectory, "System32", "WindowsPowerShell", "v1.0");
        PowerShell = Path.Combine(host, "powershell.exe");
        ModuleManifest = Path.Combine(host, "Modules", "DeliveryOptimization", "DeliveryOptimization.psd1");
    }

    /// <summary>Windows PowerShell, which runs both commands.</summary>
    public string PowerShell { get; }

    /// <summary>
    /// The module's manifest. Its presence is Delivery Optimization's commands being on this machine,
    /// which a Windows edition without the module does not have.
    /// </summary>
    public string ModuleManifest { get; }

    /// <summary>Asks Windows what the cache holds, and says why where it would not answer.</summary>
    public async Task<DeliveryOptimizationReading> ReadAsync(CancellationToken ct)
    {
        var outcome = await _runner.RunAsync(PowerShell, MeasureArguments, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return DeliveryOptimizationReading.Unanswered(outcome.Message);
        }

        // Anything but one non-negative whole number is not an answer. An empty property, which is
        // what the snapshot holds when the service reports no status, arrives as an empty line.
        return long.TryParse(
                outcome.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? DeliveryOptimizationReading.Of(bytes)
            : DeliveryOptimizationReading.Unanswered(
                string.IsNullOrWhiteSpace(outcome.StandardOutput)
                    ? "Windows gave no figure."
                    : $"Windows answered '{outcome.StandardOutput.Trim()}', which is not a size.");
    }

    public async Task<long?> MeasureAsync(CancellationToken ct) => (await ReadAsync(ct).ConfigureAwait(false)).Bytes;
}

/// <summary>What Windows said its Delivery Optimization cache holds.</summary>
/// <param name="Bytes">The cache's size, or null where Windows did not answer.</param>
/// <param name="Failure">Why Windows did not answer, written for the user, or null where it did.</param>
internal sealed record DeliveryOptimizationReading(long? Bytes, string? Failure)
{
    public static DeliveryOptimizationReading Of(long bytes) => new(bytes, null);

    public static DeliveryOptimizationReading Unanswered(string why) => new(null, why);
}
