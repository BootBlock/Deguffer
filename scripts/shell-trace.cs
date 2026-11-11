#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.2.8

// Reads the Deguffer-Shell events out of a .nettrace file that dotnet-trace recorded, and prints
// how long each of the shell's milestones took. See docs/tracing-the-shell.md.
//
//     dotnet run scripts/shell-trace.cs -- <trace.nettrace>
//
// The trace holds counts, causes and node totals only, so neither it nor this output names a file,
// a folder or a user.

using System.Globalization;
using Microsoft.Diagnostics.Tracing;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run scripts/shell-trace.cs -- <trace.nettrace>");
    return 2;
}

var raised = new List<Raised>();

using (var source = new EventPipeEventSource(args[0]))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != "Deguffer-Shell")
        {
            return;
        }

        // TraceEvent names a start or stop event by its task and opcode, as "Show/Start".
        var name = data.EventName.Replace("/", string.Empty, StringComparison.Ordinal);
        var payload = Enumerable.Range(0, data.PayloadNames.Length).Select(i => data.PayloadValue(i)).ToArray();

        raised.Add(new Raised(name, data.TimeStampRelativeMSec, payload));
    };

    source.Process();
}

if (raised.Count == 0)
{
    Console.WriteLine("No Deguffer-Shell events. Was the trace recorded with --providers Deguffer-Shell?");
    return 1;
}

Launch(raised);
Shows(raised);
var pictures = Pictures(raised);
ScanEndToFirstMap(raised, pictures);
Groups(raised);

return 0;

static void Launch(List<Raised> raised)
{
    var spans = Pairs(raised, "Launch").ToList();

    if (spans.Count == 0)
    {
        Console.WriteLine("Launch: not recorded (the trace started after the window opened).");
        return;
    }

    var launch = spans[0];
    var window = Pairs(raised, "Window").FirstOrDefault();

    // The start carries how long the process had existed when the entry point wrote it. A trace of
    // a build from before that started the launch in OnLaunched, with no payload.
    if (launch.Start.Payload is [var sinceProcessStart, ..])
    {
        var beforeEntry = Number(sinceProcessStart);

        Console.WriteLine($"Launch to first frame, from the process's creation: {Ms(beforeEntry + launch.Duration)}");
        Console.WriteLine($"  process creation to the entry point (the runtime's start): {Ms(beforeEntry)}");
        Console.WriteLine($"  entry point to OnLaunched (the stores read, the XAML parsed): {Ms(window is null ? null : window.Start.Time - launch.Start.Time)}");
    }
    else
    {
        Console.WriteLine($"OnLaunched to first frame (an older build, which started the launch there): {Ms(launch.Duration)}");
    }

    Console.WriteLine($"  main window and first page: {Ms(window?.Duration)}");

    // Reopened once the first frame is drawn, so after the launch rather than inside it.
    if (Pairs(raised, "ToolWindows").FirstOrDefault() is { } reopened)
    {
        Console.WriteLine($"Tool window reopened after the first frame: {Ms(reopened.Duration)}");
    }

    Console.WriteLine();
}

static void Shows(List<Raised> raised)
{
    var shows = Pairs(raised, "Show").ToList();

    if (shows.Count == 0)
    {
        return;
    }

    var selections = Pairs(raised, "Selection").ToList();
    var rows = Pairs(raised, "Rows").ToList();
    var handoffs = Pairs(raised, "Handoff").ToList();

    Console.WriteLine("Explore shows (UI thread held), by cause:");
    Console.WriteLine($"  {"cause",-9} {"count",5} {"median",9} {"longest",9} {"rows",9} {"selection",9} {"handoff",9}  longest at nodes, rows");

    foreach (var cause in shows.GroupBy(show => Cause(show.Start.Payload[0])))
    {
        var all = cause.ToList();
        var longest = all.MaxBy(show => show.Duration)!;
        var inside = (List<Span> parts) => parts.FirstOrDefault(part => part.Start.Time >= longest.Start.Time && part.Stop.Time <= longest.Stop.Time)?.Duration;

        Console.WriteLine(
            $"  {cause.Key,-9} {all.Count,5} {Ms(Median(all)),9} {Ms(longest.Duration),9} {Ms(inside(rows)),9} {Ms(inside(selections)),9} {Ms(inside(handoffs)),9}  {longest.Start.Payload[1]}, {longest.Stop.Payload[1]}");
    }

    Console.WriteLine();
}

static Dictionary<int, (Raised Start, Raised? Stop)> Pictures(List<Raised> raised)
{
    var pictures = new Dictionary<int, (Raised Start, Raised? Stop)>();

    foreach (var one in raised)
    {
        if (one.Name == "PictureStart")
        {
            pictures[Number(one.Payload[0])] = (one, null);
        }
        else if (one.Name == "PictureStop" && pictures.TryGetValue(Number(one.Payload[0]), out var started))
        {
            pictures[Number(one.Payload[0])] = (started.Start, one);
        }
    }

    var shown = pictures.Values.Where(p => p.Stop is { } stop && IsShown(stop.Payload[1])).ToList();
    var superseded = pictures.Values.Count(p => p.Stop is { } stop && !IsShown(stop.Payload[1]));
    var waiting = pictures.Values.Count(p => p.Stop is null);

    if (pictures.Count > 0)
    {
        Console.WriteLine($"Map pictures: {pictures.Count} handed, {shown.Count} reached the screen, {superseded} superseded first, {waiting} still waiting when the trace ended.");

        if (shown.Count > 0)
        {
            var waits = shown.Select(p => p.Stop!.Time - p.Start.Time).Order().ToList();
            Console.WriteLine($"  hand-off to first drawing on screen: median {Ms(waits[waits.Count / 2])}, longest {Ms(waits[^1])}, first {Ms(shown.MinBy(p => p.Start.Time).Stop!.Time - shown.MinBy(p => p.Start.Time).Start.Time)}");
        }

        Console.WriteLine();
    }

    return pictures;
}

static void ScanEndToFirstMap(List<Raised> raised, Dictionary<int, (Raised Start, Raised? Stop)> pictures)
{
    var finished = Pairs(raised, "Show").Where(show => Cause(show.Start.Payload[0]) == "Finished").ToList();

    foreach (var show in finished)
    {
        // The finished tree is handed to the map inside its show, so its picture starts there.
        var picture = pictures.Values
            .Where(p => p.Start.Time >= show.Start.Time && p.Start.Time <= show.Stop.Time)
            .Select(p => p.Stop)
            .FirstOrDefault(stop => stop is not null && IsShown(stop.Payload[1]));

        Console.WriteLine(picture is null
            ? $"Scan end to first map: no picture of the finished tree reached the screen (the map was not showing?)."
            : $"Scan end to first map: {Ms(picture.Time - show.Start.Time)} ({show.Start.Payload[1]} nodes)");
    }

    if (finished.Count > 0)
    {
        Console.WriteLine();
    }
}

static void Groups(List<Raised> raised)
{
    var batches = Pairs(raised, "Groups").ToList();

    if (batches.Count == 0)
    {
        return;
    }

    Console.WriteLine($"Duplicate groups: {batches.Sum(b => Number(b.Stop.Payload[0]))} applied in {batches.Count} batches, {Ms(batches.Sum(b => b.Duration))} in all, longest batch {Ms(batches.Max(b => b.Duration))}");
}

// Every pair but Picture runs on the UI thread and closes before the next of its kind opens.
static IEnumerable<Span> Pairs(List<Raised> raised, string task)
{
    Raised? open = null;

    foreach (var one in raised)
    {
        if (one.Name == task + "Start")
        {
            open = one;
        }
        else if (one.Name == task + "Stop" && open is not null)
        {
            yield return new Span(open, one);
            open = null;
        }
    }
}

static double Median(List<Span> spans) => spans.Select(s => s.Duration).Order().ElementAt(spans.Count / 2);

static bool IsShown(object outcome) => outcome is string name ? name == "Shown" : Number(outcome) == 0;

static int Number(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

// An enum arrives as its name where the reader decoded the manifest's map, and as its value otherwise.
static string Cause(object value) => value is string name ? name : Number(value) switch
{
    0 => "Snapshot",
    1 => "Step",
    2 => "Finished",
    3 => "Removal",
    var other => other.ToString(CultureInfo.InvariantCulture),
};

static string Ms(double? ms) => ms is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "-";

internal sealed record Raised(string Name, double Time, object[] Payload);

internal sealed record Span(Raised Start, Raised Stop)
{
    public double Duration => Stop.Time - Start.Time;
}
