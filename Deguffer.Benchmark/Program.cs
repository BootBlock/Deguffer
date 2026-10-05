using Deguffer.Benchmark;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

// The result goes to standard output and nothing else does, so it can be redirected to a file and
// pasted as it stands. Progress and errors go to standard error, and name no path either.

var request = BenchmarkRequest.Parse(args, out var error);
if (request is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(BenchmarkRequest.Usage);
    return 1;
}

if (request.Route.ReadsTable())
{
    var reason = TableRoutes.Probe(VolumeMftSourceFactory.Default, request.Drive);
    if (reason is not FallbackReason.None)
    {
        Console.Error.WriteLine(reason switch
        {
            FallbackReason.NotElevated => "Reading the file table needs administrator rights. Run this from an elevated prompt.",
            FallbackReason.NotNtfsVolume => "The volume is not NTFS, so it has no file table to read.",
            _ => $"The volume's file table cannot be read: {reason}.",
        });
        return 2;
    }
}
else if (LongPath.ProbeDirectory(request.Path) is not PathPresence.Present)
{
    Console.Error.WriteLine("The folder does not exist, or it cannot be read.");
    return 2;
}

Func<CancellationToken, RunTally> run = request.Route.ReadsTable()
    ? ct => TableRoutes.Run(request.Route, VolumeMftSourceFactory.Default, request.Drive, ct)
    : ct => WalkRoute.Run(request.Path, ct);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Stop between reads rather than kill the process mid-read, which is what the routes' own
    // cancellation is for.
    e.Cancel = true;
    cancel.Cancel();
};

var runs = new List<RunSample>(request.Runs);

try
{
    for (var i = 1; i <= request.Runs; i++)
    {
        Console.Error.WriteLine($"Run {i} of {request.Runs}...");
        runs.Add(Timing.Measure(run, cancel.Token));
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 3;
}
catch (IOException ex)
{
    // The table routes raise this when the volume goes away between runs. Its message names the
    // volume's state, never a path.
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Console.Out.Write(Report.Render(request.Route, MeasuredPlace.Of(request.Path), MachineFacts.Current, runs));
return 0;
