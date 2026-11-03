using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The run's measurement of one component store row: the store's actual size from a fresh DISM
/// analysis, immediately before the command and after it.
///
/// <para>The analysis after a command that succeeded is also the store as that command left it, so it
/// is recorded in <see cref="ComponentStoreSettlement"/>. That record is what stops the row offering
/// again a cleanup Windows has already done.</para>
/// </summary>
internal sealed class ComponentStoreMeasurement(
    ComponentStoreAnalysis analysis,
    ComponentStoreSettlement settlement,
    ComponentStoreCommand command) : IToolMeasurement
{
    public async Task<long?> MeasureAsync(MeasuredWhen when, CancellationToken ct)
    {
        var report = (await analysis.FreshReadingAsync(ct).ConfigureAwait(false)).Report;

        if (when is MeasuredWhen.AfterCommandSucceeded && report is not null)
        {
            settlement.Record(command, report.State);
        }

        return report?.ActualSize;
    }
}
