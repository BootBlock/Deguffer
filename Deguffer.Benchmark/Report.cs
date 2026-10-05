using System.Globalization;
using System.Text;

namespace Deguffer.Benchmark;

/// <summary>
/// The result as text that can be pasted into an issue as it stands.
///
/// <para>It is handed a <see cref="MeasuredPlace"/> and never the request's path, so no path, label,
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

    public static string Render(Route route, MeasuredPlace place, MachineFacts machine, IReadOnlyList<RunSample> runs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(runs.Count);

        var first = runs[0];
        var later = runs.Skip(1).ToArray();
        var text = new StringBuilder();

        Line(text, "Deguffer scan benchmark");
        Field(text, "route", route.ToString().ToLowerInvariant());
        Field(text, "place", place.ToString());
        Field(text, "tuning", Tuning.Describe(route));
        Field(text, "machine", Describe(machine));
        Field(text, "runs", $"{runs.Count} ({runs.Count(r => r.Tally.Complete)} complete)");

        if (machine.DebugBuild)
        {
            Field(text, "warning", "a Debug build. Build with -c Release before reporting a result.");
        }

        Line(text);
        Row(text, string.Empty, "first run", later.Length > 0 ? "later runs: median (min to max)" : string.Empty);

        Row(text, "elapsed", Duration(first.Elapsed.TotalSeconds), Over(later, r => r.Elapsed.TotalSeconds, Duration));
        Row(text, $"{route.ItemName()}/s", Count(first.ItemsPerSecond), Over(later, r => r.ItemsPerSecond, Count));

        if (route.ReadsTable())
        {
            Row(text, "MiB/s read", Rate(first.MegabytesPerSecond), Over(later, r => r.MegabytesPerSecond, Rate));
        }

        Row(text, "allocated", Bytes(first.AllocatedBytes), Over(later, r => r.AllocatedBytes, Bytes));
        Row(text, "peak working set", Bytes(first.PeakWorkingSet), later.Length > 0 ? Bytes(later.Max(r => r.PeakWorkingSet)) : string.Empty);
        Row(text, route.ItemName(), Count(first.Tally.Items), Over(later, r => r.Tally.Items, Count));

        return text.ToString();
    }

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

    private static string Rate(double value) => string.Create(Invariant, $"{value:N1}");

    private static string Bytes(double bytes) => string.Create(Invariant, $"{bytes / (1024 * 1024):N1} MiB");

    private static void Field(StringBuilder text, string label, string value) =>
        Line(text, label.PadRight(LabelWidth) + value);

    private static void Row(StringBuilder text, string label, string first, string later) =>
        Line(text, (label.PadRight(LabelWidth) + first.PadRight(FirstWidth) + later).TrimEnd());

    private static void Line(StringBuilder text, string line = "") => text.Append(line).Append('\n');
}
