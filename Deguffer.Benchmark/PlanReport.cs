using System.Text;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution.Timing;

namespace Deguffer.Benchmark;

/// <summary>
/// The plan route's part of the result: the costly questions the pass asked Windows (#516), and when
/// each provider took a slot and ended (#443). Apart from <see cref="Report"/>, which states what
/// every route reports, and in its formats.
/// </summary>
internal static class PlanReport
{
    /// <summary>The machine calls, then the providers, under the rows every route reports.</summary>
    public static void Rows(StringBuilder text, RunSample first, RunSample[] later)
    {
        CallRows(text, first, later);
        ProviderRows(text, first, later);
    }

    /// <summary>
    /// When each provider of a planning pass took a slot, when it ended and how long it took, from the
    /// start of the pass, in the order the first run started them, which is the order the pass handed
    /// out its slots. Named by id, which is a fixed word and never a place. The later runs' columns are
    /// each the median of that column alone, so a row's start, end and time need not add up there.
    /// </summary>
    private static void ProviderRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        var times = PlanOf(first).Times
            .OrderBy(time => time.Began)
            .ThenBy(time => time.Provider.Id, StringComparer.Ordinal)
            .ToArray();
        var idWidth = Math.Max("provider".Length, times.Length == 0 ? 0 : times.Max(time => time.Provider.Id.Length)) + 2;

        Report.Line(text);
        PlanRow(text, idWidth, string.Empty, ["first run", string.Empty, string.Empty, later.Length > 0 ? "later runs: median" : string.Empty]);
        PlanRow(text, idWidth, "provider", ["start", "end", "took", .. later.Length > 0 ? new[] { "start", "end", "took" } : []]);

        foreach (var time in times)
        {
            var id = time.Provider.Id;
            string[] columns =
            [
                Report.Duration(time.Began.TotalSeconds),
                Report.Duration(time.Ended.TotalSeconds),
                Report.Duration(time.Elapsed.TotalSeconds),
                .. later.Length > 0
                    ? new[]
                    {
                        Report.Duration(Median(later, r => TimeOf(r, id).Began)),
                        Report.Duration(Median(later, r => TimeOf(r, id).Ended)),
                        Report.Duration(Median(later, r => TimeOf(r, id).Elapsed)),
                    }
                    : [],
            ];

            PlanRow(text, idWidth, id, columns);
        }
    }

    /// <summary>
    /// How many of each costly question to Windows a planning pass asked, and the time they took summed,
    /// so calls made side by side each count their own (#516). Counts and times only, never a place.
    /// </summary>
    private static void CallRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        var calls = Enum.GetValues<MachineCall>();
        var width = calls.Max(call => call.ToString().Length) + 2;

        Report.Line(text);
        PlanRow(text, width, string.Empty, ["first run", string.Empty, later.Length > 0 ? "later runs: median" : string.Empty]);
        PlanRow(text, width, "machine call", ["calls", "took", .. later.Length > 0 ? new[] { "calls", "took" } : []]);

        foreach (var call in calls)
        {
            var counted = PlanOf(first).Of(call);
            string[] columns =
            [
                Report.Count(counted.Calls),
                Report.Duration(counted.Elapsed.TotalSeconds),
                .. later.Length > 0
                    ? new[]
                    {
                        Report.Count(Spread.Of([.. later.Select(r => (double)PlanOf(r).Of(call).Calls)]).Median),
                        Report.Duration(Median(later, r => PlanOf(r).Of(call).Elapsed)),
                    }
                    : [],
            ];

            PlanRow(text, width, call.ToString(), columns);
        }
    }

    private static double Median(RunSample[] runs, Func<RunSample, TimeSpan> measure) =>
        Spread.Of([.. runs.Select(run => measure(run).TotalSeconds)]).Median;

    /// <summary>
    /// A run's pass. Every run of the plan route records one, so a run without it was not measured as
    /// the others were, and no median of them holds.
    /// </summary>
    private static PlanRun PlanOf(RunSample run) =>
        run.Tally.Planning ?? throw new InvalidOperationException("Every run of the plan route carries its providers' times.");

    /// <summary>A later run's time for one provider. Every pass plans every provider, so one missing was a different pass.</summary>
    private static ProviderPlanTime TimeOf(RunSample run, string id) =>
        PlanOf(run).Of(id) ?? throw new InvalidOperationException("Every run of the plan route plans the same providers.");

    private const int PlanColumnWidth = 11;

    private static void PlanRow(StringBuilder text, int idWidth, string id, IReadOnlyList<string> columns)
    {
        var line = new StringBuilder(id.PadRight(idWidth));

        foreach (var column in columns)
        {
            line.Append(column.PadRight(PlanColumnWidth));
        }

        Report.Line(text, line.ToString().TrimEnd());
    }
}
