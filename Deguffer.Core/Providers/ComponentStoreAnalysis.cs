using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// DISM's analysis of the component store, and the tool that runs it and the store's cleanups.
///
/// <para><b>One analysis for both component store rows.</b> The analysis is read-only but takes a
/// minute or more, and the cleanup and the reset ask it the same question, so the planner builds one
/// and hands it to both. What a planning pass read is kept until the next pass clears it (G4). What
/// the run asks of <see cref="MeasureAsync"/> is never kept: it is a figure subtracted from another,
/// and a remembered one would report nothing freed.</para>
///
/// <para><b>Only an administrator is answered.</b> DISM refuses every other account with exit code
/// 740 before it looks at anything, which is a reason to elevate rather than a failure to report.</para>
/// </summary>
public sealed class ComponentStoreAnalysis(ISystemDirectories system, IProcessRunner runner) : IToolMeasurement
{
    /// <summary>The read-only analysis, in English so its labels can be read.</summary>
    public const string AnalyzeArguments = "/Online /English /Cleanup-Image /AnalyzeComponentStore";

    /// <summary>DISM's exit code where the account is not an administrator.</summary>
    public const int ElevationRequired = 740;

    private Task<ComponentStoreReading>? _reading;

    /// <summary>DISM, as Windows ships it for this machine's architecture.</summary>
    public string Dism { get; } = NativeSystemTool.In(system, "Dism.exe");

    /// <summary>What the store held when this planning pass first asked, asked once per pass.</summary>
    public Task<ComponentStoreReading> ReadingAsync(CancellationToken ct) => _reading ??= ReadAsync(ct);

    /// <summary>Forgets the last pass's reading, for a planning pass that starts afresh.</summary>
    public void Invalidate() => _reading = null;

    /// <summary>The store's actual size now, from a fresh analysis, or null where DISM did not give one.</summary>
    public async Task<long?> MeasureAsync(CancellationToken ct) =>
        (await ReadAsync(ct).ConfigureAwait(false)).Report?.ActualSize;

    private async Task<ComponentStoreReading> ReadAsync(CancellationToken ct)
    {
        var outcome = await runner.RunAsync(Dism, AnalyzeArguments, ct).ConfigureAwait(false);

        if (outcome.ExitCode == ElevationRequired)
        {
            return new ComponentStoreReading(null, null, NeedsElevation: true);
        }

        if (!outcome.Succeeded)
        {
            return new ComponentStoreReading(null, Reason(outcome.Message), NeedsElevation: false);
        }

        return ComponentStoreReport.Parse(outcome.StandardOutput) is { } report
            ? new ComponentStoreReading(report, null, NeedsElevation: false)
            : new ComponentStoreReading(null, "DISM's report did not include every figure.", NeedsElevation: false);
    }

    /// <summary>
    /// DISM's reason, which it writes as the last lines of its output after its banner and any progress
    /// bars, and which reads as a sentence without them.
    /// </summary>
    private static string Reason(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = Array.FindLastIndex(lines, line => line.StartsWith("Error:", StringComparison.Ordinal));

        return error >= 0 ? string.Join(' ', lines[error..]) : lines.Length > 0 ? lines[^1] : message;
    }
}

/// <summary>What DISM said about the component store.</summary>
/// <param name="Report">The analysis, or null where DISM gave none.</param>
/// <param name="Failure">Why DISM gave none, written for the user, or null where it did or where it wanted elevation.</param>
/// <param name="NeedsElevation">Whether DISM refused because the account is not an administrator.</param>
public sealed record ComponentStoreReading(ComponentStoreReport? Report, string? Failure, bool NeedsElevation);
