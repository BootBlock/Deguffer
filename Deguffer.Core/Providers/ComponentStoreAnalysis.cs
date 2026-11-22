using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// DISM's analysis of the component store, asked once per planning pass for the plans and afresh for
/// each figure the run subtracts (see <see cref="ComponentStoreMeasurement"/>).
///
/// <para><b>One analysis for both component store rows.</b> The analysis is read-only but takes a
/// minute or more, and the cleanup and the reset ask it the same question, so the planner builds one
/// and hands it to both. What a planning pass read is kept until the next pass clears it (G4). What
/// a clean asks of <see cref="RunReadingAsync"/> is a figure subtracted from another, so it stands only
/// until anything in the clean acts. A command acts, so the figure after it is always asked afresh, and
/// a remembered figure can never make a command look as if it freed nothing.</para>
///
/// <para><b>Every call is recorded in <see cref="OwnServicingCalls"/>.</b> Windows starts its servicing
/// stack for the analysis and keeps it running for minutes afterwards, and the hold on an unfinished
/// update would otherwise take that stack for an update and refuse the cleanup the analysis was
/// measuring for.</para>
///
/// <para><b>Only an administrator is answered.</b> DISM refuses every other account with exit code
/// 740 before it looks at anything, which is a reason to elevate rather than a failure to report.</para>
/// </summary>
public sealed class ComponentStoreAnalysis(
    ISystemDirectories system,
    IProcessRunner runner,
    OwnServicingCalls servicingCalls)
{
    /// <summary>The read-only analysis, in English so its labels can be read.</summary>
    public const string AnalyzeArguments = "/Online /English /Cleanup-Image /AnalyzeComponentStore";

    /// <summary>
    /// The analysis is read-only, so a cancelled scan ends it rather than leave DISM analysing beside
    /// the next scan's own analysis. It takes a minute or more, and many on a slow disk, so it is given
    /// far longer than an ordinary query before it is taken to have hung.
    /// </summary>
    internal static readonly ToolRun AnalysisRun = ToolRun.QueryWithin(TimeSpan.FromMinutes(30));

    /// <summary>DISM's exit code where the account is not an administrator.</summary>
    public const int ElevationRequired = 740;

    private readonly PassMemo<Task<ComponentStoreReading>> _reading = new();

    private KeptReading? _lastInRun;

    /// <summary>DISM, as Windows ships it for this machine's architecture.</summary>
    public string Dism { get; } = NativeSystemTool.In(system, "Dism.exe");

    /// <summary>
    /// What the store held when this planning pass first asked, asked once per pass.
    ///
    /// <para>The analysis runs under the token of whoever asked first, which is the whole pass's where
    /// <see cref="ComponentStoreProviderBase.BeginPass"/> started it, and every caller waits until DISM
    /// has ended, cancelled or not, so none returns while it is still running.</para>
    /// </summary>
    public Task<ComponentStoreReading> ReadingAsync(CancellationToken ct) => _reading.Get(() => ReadAsync(ct));

    /// <summary>Forgets the last pass's reading, for a planning pass that starts afresh.</summary>
    public void Invalidate() => _reading.Forget();

    /// <summary>
    /// What the store holds now, for a clean: from an analysis asked afresh, or from the last one this
    /// clean asked where nothing in it has acted since that analysis began.
    ///
    /// <para><b>Why the last one can stand.</b> The cleanup's analysis after its command and the reset's
    /// before its own are adjacent in a clean of both rows, and nothing runs between them, so asking
    /// again would spend a minute or more reading what was just read. <paramref name="run"/> says
    /// whether anything ran: a command, a removal, a Disk Cleanup handler or a servicing call of the
    /// clean's own, from any plan in it. Any of those, a reading begun while one acted, or a clean other
    /// than the one the reading was taken in, asks afresh.</para>
    ///
    /// <para>Only an analysis that gave a report is kept. A failure or a refusal is asked again, because
    /// an answer the run could not use is no saving, and the next analysis may give one.</para>
    /// </summary>
    public async Task<ComponentStoreReading> RunReadingAsync(RunActivity run, CancellationToken ct)
    {
        if (Volatile.Read(ref _lastInRun) is { } last && run.StillQuiet(last.Mark))
        {
            return last.Reading;
        }

        // Taken before DISM starts, so a step that starts acting while DISM reads breaks the mark.
        var mark = run.Quiet();
        var reading = await ReadAsync(ct).ConfigureAwait(false);

        if (mark is { } quiet && reading.Report is not null)
        {
            Volatile.Write(ref _lastInRun, new KeptReading(quiet, reading));
        }

        return reading;
    }

    private async Task<ComponentStoreReading> ReadAsync(CancellationToken ct)
    {
        var outcome = await servicingCalls.MakeAsync(() => runner.RunAsync(Dism, AnalyzeArguments, AnalysisRun, ct)).ConfigureAwait(false);

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

    /// <summary>A clean's last analysis, and where the clean's activity stood as it began.</summary>
    private sealed record KeptReading(QuietMark Mark, ComponentStoreReading Reading);
}

/// <summary>What DISM said about the component store.</summary>
/// <param name="Report">The analysis, or null where DISM gave none.</param>
/// <param name="Failure">Why DISM gave none, written for the user, or null where it did or where it wanted elevation.</param>
/// <param name="NeedsElevation">Whether DISM refused because the account is not an administrator.</param>
public sealed record ComponentStoreReading(ComponentStoreReport? Report, string? Failure, bool NeedsElevation);
