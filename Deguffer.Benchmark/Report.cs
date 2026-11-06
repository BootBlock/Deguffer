using System.Globalization;
using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Benchmark;

/// <summary>
/// The result as text that can be pasted into an issue as it stands.
///
/// <para>It is handed <see cref="MeasuredPlace"/>s and never the request's paths, so no path, label,
/// user or machine name can reach it.</para>
///
/// <para>The first run is reported apart from the rest because the two answer different questions.
/// For the walk, the first run lists the folders from the drive and later runs largely from the
/// system file cache. The table is read through a volume handle, which Windows does not cache, so
/// for those routes the later runs mostly measure the drive again.</para>
/// </summary>
internal static class Report
{
    private const int LabelWidth = 19;
    private const int FirstWidth = 16;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <param name="places">Every drive the route measured on, each once.</param>
    /// <param name="census">
    /// How many records the table holds in use, for a route that reads it, or null where its bitmap
    /// could not be read.
    /// </param>
    public static string Render(
        Route route,
        string tuning,
        IReadOnlyList<MeasuredPlace> places,
        MachineFacts machine,
        IReadOnlyList<RunSample> runs,
        TableCensus? census = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(runs.Count);

        var first = runs[0];
        var later = runs.Skip(1).ToArray();
        var text = new StringBuilder();

        Line(text, "Deguffer benchmark");
        Field(text, "route", route.ToString().ToLowerInvariant());
        Field(text, places.Count == 1 ? "place" : "places", string.Join(", ", places));
        Field(text, "tuning", tuning);
        Field(text, "machine", Describe(machine));
        Field(text, "collector", machine.Collector.ToString());
        Field(text, "runs", $"{runs.Count} ({runs.Count(r => r.Tally.Complete)} complete)");

        if (route.ReadsTable())
        {
            Field(text, "records in use", census is { } counted ? Count(counted.RecordsInUse) : "unknown: the bitmap could not be read");
        }

        if (machine.DebugBuild)
        {
            Field(text, "warning", "a Debug build. Build with -c Release before reporting a result.");
        }

        Line(text);
        Row(text, string.Empty, "first run", later.Length > 0 ? "later runs: median (min to max)" : string.Empty);

        var itemName = first.Tally.Race is { } race ? race.Racer.ItemName() : route.ItemName();

        if (route is Route.Race)
        {
            // A round is four timed parts and the untimed walks before two of them, so its time is the
            // rate of nothing. The parts are below.
            Row(text, "round", Duration(first.Elapsed.TotalSeconds), Over(later, r => r.Elapsed.TotalSeconds, Duration));
            RaceRows(text, first, later);
        }
        else
        {
            Row(text, "elapsed", Duration(first.Elapsed.TotalSeconds), Over(later, r => r.Elapsed.TotalSeconds, Duration));
            Row(text, $"{itemName}/s", Count(first.ItemsPerSecond), Over(later, r => r.ItemsPerSecond, Count));
        }

        if (route.ReadsTable() && route is not Route.Race)
        {
            Row(text, "MiB/s read", Rate(first.MegabytesPerSecond), Over(later, r => r.MegabytesPerSecond, Rate));
            Row(text, "reads", Count(first.Tally.Reads), Over(later, r => r.Tally.Reads, Count));
            Row(text, "cluster reads", Count(first.Tally.ClusterReads), Over(later, r => r.Tally.ClusterReads, Count));
            Row(
                text,
                "list reads took",
                Duration(first.Tally.ListReadTime.TotalSeconds),
                Over(later, r => r.Tally.ListReadTime.TotalSeconds, Duration));

            if (census is { } inUse)
            {
                Row(text, "free records read", Bytes(inUse.FreeBytesRead(first.Tally)), Over(later, r => inUse.FreeBytesRead(r.Tally), Bytes));
            }

            Row(text, "mean read", Kibibytes(first.Tally.MeanReadBytes), Over(later, r => r.Tally.MeanReadBytes, Kibibytes));
            StageRows(text, first, later);
        }
        else if (route.Removes())
        {
            RemovalRows(text, first, later);
        }
        else if (route is not Route.Race)
        {
            Row(text, "folders at once", Count(first.Tally.PeakListings), Over(later, r => r.Tally.PeakListings, Count));
        }

        // Over a race's whole round these would count its untimed walks and the collections it forces
        // between parts, so the race reports them for each walk instead (RaceRows).
        if (route is not Route.Race)
        {
            Row(text, "processors busy", Share(first.ProcessorsBusy), Over(later, r => r.ProcessorsBusy, Share));
            Row(text, "allocated", Bytes(first.AllocatedBytes), Over(later, r => r.AllocatedBytes, Bytes));

            if (route.KeepsResult())
            {
                Row(text, "kept", Bytes(first.KeptBytes), Over(later, r => r.KeptBytes, Bytes));
            }

            Row(text, "full collections", Count(first.FullCollections), Over(later, r => r.FullCollections, Count));
        }

        Row(text, "peak working set", Bytes(first.PeakWorkingSet), later.Length > 0 ? Bytes(later.Max(r => r.PeakWorkingSet)) : string.Empty);
        Row(text, itemName, Count(first.Tally.Items), Over(later, r => r.Tally.Items, Count));

        return text.ToString();
    }

    /// <summary>
    /// Each part of a race's rounds: the walk and the build alone and raced, and how many times as long
    /// each took raced. Then what the collector did while each walk ran, process-wide: a collection the
    /// build's allocations start suspends the walk's threads too, so the time paused while the walk
    /// ran is time the walk could not use, and the collections say which generations it went on.
    /// </summary>
    private static void RaceRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        var race = RaceOf(first);

        Row(text, "walk alone", Duration(race.WalkAlone.Elapsed.TotalSeconds), Over(later, r => RaceOf(r).WalkAlone.Elapsed.TotalSeconds, Duration));
        Row(text, "walk raced", Duration(race.WalkRaced.Elapsed.TotalSeconds), Over(later, r => RaceOf(r).WalkRaced.Elapsed.TotalSeconds, Duration));
        Row(text, "  times as long", Times(race.Slowdown), Over(later, r => RaceOf(r).Slowdown, Times));
        Row(text, "build alone", Duration(race.BuildAlone.Elapsed.TotalSeconds), Over(later, r => RaceOf(r).BuildAlone.Elapsed.TotalSeconds, Duration));
        Row(text, "build raced", Duration(race.BuildRaced.Elapsed.TotalSeconds), Over(later, r => RaceOf(r).BuildRaced.Elapsed.TotalSeconds, Duration));
        Row(text, "  times as long", Times(race.BuildSlowdown), Over(later, r => RaceOf(r).BuildSlowdown, Times));

        HeapRows(text, "walk alone", first, later, static times => times.WalkAlone.Heap);
        HeapRows(text, "walk raced", first, later, static times => times.WalkRaced.Heap);
    }

    /// <summary>What the collector did while one part ran, under a heading naming the part.</summary>
    private static void HeapRows(StringBuilder text, string part, RunSample first, RunSample[] later, Func<RaceTimes, HeapCounters> heap)
    {
        var counters = heap(RaceOf(first));

        Line(text, part);
        Row(text, "  paused", Duration(counters.Paused.TotalSeconds), Over(later, r => heap(RaceOf(r)).Paused.TotalSeconds, Duration));
        Row(text, "  gen 0 only", Count(counters.Gen0Only), Over(later, r => heap(RaceOf(r)).Gen0Only, Count));
        Row(text, "  gen 1 only", Count(counters.Gen1Only), Over(later, r => heap(RaceOf(r)).Gen1Only, Count));
        Row(text, "  gen 2", Count(counters.Gen2), Over(later, r => heap(RaceOf(r)).Gen2, Count));
        Row(text, "  allocated", Bytes(counters.Allocated), Over(later, r => heap(RaceOf(r)).Allocated, Bytes));
    }

    /// <summary>
    /// A run's race. Every round of the race route is timed part by part, so a run without its parts
    /// was not measured as the others were, and no median of them holds.
    /// </summary>
    private static RaceTimes RaceOf(RunSample run) =>
        run.Tally.Race ?? throw new InvalidOperationException("Every run of the race route carries its parts.");

    /// <summary>
    /// Where a table route's time went: opening the volume, each stage in the order the first run ran
    /// them, and what neither covers, so the rows add up to the elapsed time. Then the counts that
    /// size the stages run on one thread after the drive has finished. A stage a run stopped before
    /// reads as zero.
    /// </summary>
    private static void StageRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        if (first.Tally.Stages is not { } stages)
        {
            return;
        }

        Row(text, "  volume open", Duration(first.Tally.OpenTime.TotalSeconds), Over(later, r => r.Tally.OpenTime.TotalSeconds, Duration));
        OpenRows(text, first, later);

        foreach (var timed in stages.Stages)
        {
            var stage = timed.Stage;
            Row(text, StageName(stage), Duration(timed.Elapsed.TotalSeconds), Over(later, r => StageSeconds(r, stage), Duration));
        }

        Row(text, "  the rest", Duration(Rest(first)), Over(later, Rest, Duration));
        Row(text, "records deferred", Count(stages.DeferredRecords), Over(later, r => StagesOf(r).DeferredRecords, Count));

        if (stages.Stages.Any(timed => timed.Stage == MftBuildStage.LinkPlacement))
        {
            Row(text, "links placed", Count(stages.LinksPlaced), Over(later, r => StagesOf(r).LinksPlaced, Count));
        }
    }

    /// <summary>
    /// Each step of the open, in the order the first run ran them, and under each step that read the
    /// volume, how many reads it asked for. The steps add up to the open less the moments around it.
    /// None where the volume was opened by something that does not time its steps, and a step a run
    /// stopped before reads as zero.
    /// </summary>
    private static void OpenRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        if (first.Tally.Open is not { } open)
        {
            return;
        }

        foreach (var timed in open.Steps)
        {
            var step = timed.Step;
            Row(text, OpenStepName(step), Duration(timed.Elapsed.TotalSeconds), Over(later, r => OpenStep(r, step).Elapsed.TotalSeconds, Duration));

            if (timed.Reads > 0)
            {
                Row(text, "      reads", Count(timed.Reads), Over(later, r => OpenStep(r, step).Reads, Count));
            }
        }
    }

    private static MftOpenStepTime OpenStep(RunSample run, MftOpenStep step) => run.Tally.Open?.Of(step) ?? default;

    private static string OpenStepName(MftOpenStep step) => step switch
    {
        MftOpenStep.VolumeHandle => "    handle",
        MftOpenStep.BootSector => "    boot sector",
        MftOpenStep.FirstRecord => "    record 0",
        MftOpenStep.AttributeList => "    list",
        MftOpenStep.ExtensionRecords => "    extensions",
        MftOpenStep.BitmapPlacement => "    bitmap pieces",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a step of opening a table."),
    };

    /// <summary>
    /// The run's time outside the open and the stages: what the route does before the first stage
    /// and after the last, such as allocating the index's columns, and closing the volume.
    /// </summary>
    private static double Rest(RunSample run) =>
        (run.Elapsed - run.Tally.OpenTime - StagesOf(run).Stages.Aggregate(TimeSpan.Zero, (sum, timed) => sum + timed.Elapsed)).TotalSeconds;

    private static double StageSeconds(RunSample run, MftBuildStage stage) => StagesOf(run).Of(stage).TotalSeconds;

    /// <summary>
    /// A later run's stages. Every run of a table route is timed, so where the first run carries its
    /// stages and a later run does not, the runs were not measured alike and no median of them holds.
    /// </summary>
    private static MftStageTimings StagesOf(RunSample run) =>
        run.Tally.Stages ?? throw new InvalidOperationException("Every run of a table route carries its stage timings.");

    private static string StageName(MftBuildStage stage) => stage switch
    {
        MftBuildStage.BitmapRead => "  bitmap read",
        MftBuildStage.FirstPass => "  first pass",
        MftBuildStage.ExtensionLists => "  extension lists",
        MftBuildStage.DeferredRecords => "  deferred records",
        MftBuildStage.LinkPlacement => "  link placing",
        MftBuildStage.ChildLinks => "  child links",
        MftBuildStage.TreeAssembly => "  tree assembly",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a stage of a table read."),
    };

    /// <summary>
    /// Where a removal route's time went: the clean route's steps around their removals where it ran
    /// any, then the removals' three passes, each summed over every removal of the run. A removal runs
    /// its passes beside each other, so they add to more than its time.
    /// </summary>
    private static void RemovalRows(StringBuilder text, RunSample first, RunSample[] later)
    {
        if (first.Tally.Steps is not null)
        {
            Row(text, "per step", Duration(PerStep(first)), Over(later, PerStep, Duration));
            Row(text, "use checks", Duration(StepPart(first, s => s.UseCheck)), Over(later, r => StepPart(r, s => s.UseCheck), Duration));
            Row(text, "looks", Duration(StepPart(first, s => s.Look)), Over(later, r => StepPart(r, s => s.Look), Duration));
            Row(text, "acting", Duration(StepPart(first, s => s.Acting)), Over(later, r => StepPart(r, s => s.Acting), Duration));
        }

        Row(text, "walk", Duration(Pass(first, p => p.Walk)), Over(later, r => Pass(r, p => p.Walk), Duration));
        Row(text, "file pass", Duration(Pass(first, p => p.Files)), Over(later, r => Pass(r, p => p.Files), Duration));
        Row(text, "folder pass", Duration(Pass(first, p => p.Folders)), Over(later, r => Pass(r, p => p.Folders), Duration));
        Row(text, "processor time", Duration(first.ProcessorTime.TotalSeconds), Over(later, r => r.ProcessorTime.TotalSeconds, Duration));
        Row(text, "worker threads", Count(first.Tally.PeakWorkers), Over(later, r => r.Tally.PeakWorkers, Count));
    }

    /// <summary>A pass's seconds, or zero for a run whose removals never walked a tree.</summary>
    private static double Pass(RunSample run, Func<RemovalPasses, PassTiming> pass) =>
        run.Tally.Passes is { } passes ? pass(passes).Elapsed.TotalSeconds : 0;

    private static double StepPart(RunSample run, Func<StepTimes, TimeSpan> part) => part(run.Tally.Steps!).TotalSeconds;

    /// <summary>The steps' time over how many there were, so a plan of any size reads the same.</summary>
    private static double PerStep(RunSample run) =>
        run.Tally.Steps is { Count: > 0 } steps ? steps.Elapsed.TotalSeconds / steps.Count : 0;

    private static string Describe(MachineFacts machine) => string.Create(
        Invariant,
        $"{machine.Processors} logical processors, {machine.Architecture}, Windows {machine.Windows}, "
        + $".NET {machine.Runtime}, {(machine.Elevated ? "elevated" : "not elevated")}");

    /// <summary>The median of the later runs, and their range, or nothing where there were none.</summary>
    private static string Over(RunSample[] later, Func<RunSample, double> measure, Func<double, string> format)
    {
        if (later.Length == 0)
        {
            return string.Empty;
        }

        var spread = Spread.Of([.. later.Select(measure)]);

        return $"{format(spread.Median)} ({format(spread.Minimum)} to {format(spread.Maximum)})";
    }

    private static string Duration(double seconds) => seconds < 1
        ? string.Create(Invariant, $"{seconds * 1000:N1} ms")
        : string.Create(Invariant, $"{seconds:N3} s");

    private static string Count(double value) => string.Create(Invariant, $"{value:N0}");

    private static string Times(double ratio) => string.Create(Invariant, $"{ratio:N2}x");

    private static string Share(double processors) => string.Create(Invariant, $"{processors:N2}");

    private static string Rate(double value) => string.Create(Invariant, $"{value:N1}");

    private static string Kibibytes(double bytes) => string.Create(Invariant, $"{bytes / 1024:N1} KiB");

    private static string Bytes(double bytes) => string.Create(Invariant, $"{bytes / (1024 * 1024):N1} MiB");

    private static void Field(StringBuilder text, string label, string value) =>
        Line(text, label.PadRight(LabelWidth) + value);

    private static void Row(StringBuilder text, string label, string first, string later) =>
        Line(text, (label.PadRight(LabelWidth) + first.PadRight(FirstWidth) + later).TrimEnd());

    private static void Line(StringBuilder text, string line = "") => text.Append(line).Append('\n');
}
