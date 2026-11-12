using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Windows says its Delivery Optimization cache holds, and the command that clears it.
///
/// <para><b>The figure is the service's own.</b> The cache is in the Network Service profile, which
/// refuses the signed-in account a listing, and a policy can move it to another drive, so nothing
/// Deguffer could walk gives its size. <see cref="IDeliveryOptimizationStatus"/> asks the service, which
/// answers without elevation and without walking anything, so the figure is exact.</para>
///
/// <para><b>The clear is Windows PowerShell's, from the Windows directory</b> (§5.1). The
/// <c>DeliveryOptimization</c> module ships with Windows PowerShell under <c>System32</c>, so that host
/// is named by where Windows put it rather than found on <c>PATH</c>, and through
/// <see cref="NativeSystemTool"/>, so a 32-bit Deguffer runs the host and reads the module built for the
/// running Windows rather than their 32-bit copies. The in-box module loads under every execution
/// policy, including <c>Restricted</c>, which was observed on Windows 11 24H2, so no policy is
/// overridden.</para>
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

    private readonly IDeliveryOptimizationStatus _status;

    public DeliveryOptimizationCache(ISystemDirectories system, IDeliveryOptimizationStatus status)
    {
        _status = status;

        var host = NativeSystemTool.In(system, Path.Combine("WindowsPowerShell", "v1.0"));
        PowerShell = Path.Combine(host, "powershell.exe");
        ModuleManifest = Path.Combine(host, "Modules", "DeliveryOptimization", "DeliveryOptimization.psd1");
    }

    /// <summary>Windows PowerShell, which runs the clear.</summary>
    public string PowerShell { get; }

    /// <summary>
    /// The module's manifest. Its presence is Delivery Optimization's commands being on this machine,
    /// which a Windows edition without the module does not have.
    /// </summary>
    public string ModuleManifest { get; }

    /// <summary>Asks Windows what the cache holds, off the caller's thread, and says why where it would not answer.</summary>
    public Task<DeliveryOptimizationReading> ReadAsync(CancellationToken ct) => Task.Run(() => _status.Read(ct), ct);

    public async Task<long?> MeasureAsync(MeasuredWhen when, CancellationToken ct) => (await ReadAsync(ct).ConfigureAwait(false)).Bytes;
}
