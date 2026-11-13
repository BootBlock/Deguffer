using Deguffer.Benchmark;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

// The result goes to standard output and nothing else does, so it can be redirected to a file and
// pasted as it stands. Progress and errors go to standard error, and name no path either.

// Asks each device what kind of drive it is, as the app's tuner does, and only for the race, whose
// values are a scan's for each drive's kind.
var tuner = new ScanTuner(DefaultPreferences.Instance, new VolumeMediaCache(StorageQueries.Machine), VolumeInventory.Current);

var request = BenchmarkRequest.Parse(args, tuner, out var error);
if (request is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage.Text);
    return 1;
}

TableCensus? census = null;
RemovalSite? site = null;
ChecksumStoreFolder? store = null;

if (request.Route.ReadsTable())
{
    var reason = TableRoutes.Probe(VolumeMftSourceFactory.Default, request.Drive, out census);
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

    if (request.Route is Route.Race or Route.Scan && LongPath.ProbeDirectory(request.Path) is not PathPresence.Present)
    {
        Console.Error.WriteLine("The folder does not exist, or it cannot be read.");
        return 2;
    }
}
else if (request.Route.Builds())
{
    site = RemovalSite.Create(request.Path, out var refused);
    if (site is null)
    {
        Console.Error.WriteLine(refused);
        return 2;
    }
}
else if (request.Folders.Any(folder => LongPath.ProbeDirectory(folder) is not PathPresence.Present))
{
    Console.Error.WriteLine(request.Folders.Count == 1
        ? "The folder does not exist, or it cannot be read."
        : "A folder does not exist, or it cannot be read.");
    return 2;
}

if (request.Route is Route.Duplicates)
{
    try
    {
        store = ChecksumStoreFolder.Create(UserEnvironment.Current);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Its message names the temporary folder, which names the account, so only the error is repeated.
        Console.Error.WriteLine($"The folder for the route's checksums could not be created in the temporary folder: error 0x{ex.HResult:X8}.");
        return 2;
    }
}

try
{
    return await BenchmarkAsync(request, tuner, site, store, census);
}
finally
{
    // Whatever stopped the runs, so the folder this created is not left for the person who ran it to
    // find. What could not be removed is inside the folder they named, and nowhere else.
    if (site is not null && !site.RemoveAll())
    {
        Console.Error.WriteLine("The folder the benchmark created could not be removed in full. What is left is inside it.");
    }

    if (store is not null && !await store.RemoveAsync())
    {
        Console.Error.WriteLine("The folder the benchmark kept its checksums in, in the temporary folder, could not be removed in full.");
    }
}

static async Task<int> BenchmarkAsync(BenchmarkRequest request, ScanTuner tuner, RemovalSite? site, ChecksumStoreFolder? store, TableCensus? census)
{
    var built = $@"{request.Drive}:\";
    var relation = request.Route is Route.Race
        ? DiskRelations.Of(request.Path[0], tuner.StorageOf(request.Path), request.Drive, tuner.StorageOf(built))
        : DiskRelation.Unknown;
    var buildYields = RaceRoute.BuildYields(request.BuildYields, relation);

    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // Stop between reads rather than kill the process mid-read, which is what the routes' own
        // cancellation is for. A removal route's folder is then removed as the process ends.
        e.Cancel = true;
        cancel.Cancel();
    };

    var runs = new List<RunSample>(request.Runs);

    try
    {
        // Inside the try, because the duplicates route asks this machine's protections before its
        // runs, which a cancel can stop.
        var route = request.Route switch
        {
            Route.Walk => RouteRun.Blocking(ct => WalkRoute.Run(request.Path, request.Walk, ct)),
            Route.Measure => RouteRun.Blocking(ct => MeasureRoute.Run(request.Path, request.Walk, ct)),
            Route.ExploreWalk => RouteRun.Blocking(ct => ExploreWalkRoute.Run(request.Path, request.Walk, request.Snapshots, ct)),
            Route.Sources => RouteRun.Blocking(ct => SourcesRoute.Run(request.Path, request.Walk, ct)),
            Route.Links => RouteRun.Blocking(ct => LinksRoute.Run(request.Path, request.ReadBy, ct)),
            Route.Folders => RouteRun.Blocking(ct => FoldersRoute.Run(VolumeMftSourceFactory.Default, request.Folders, ct)),
            Route.Duplicates => (await DuplicatesRoute.OnThisMachineAsync(
                request.Folders, request.Checksum, request.Cache, store!.Store, tuner, cancel.Token)).Timed,
            Route.Remove => new RemoveRoute(site!, request.Tree).Timed,
            Route.Clean => new CleanRoute(
                site!, request.Trees, request.Tree, UserEnvironment.Current, SystemDirectories.Current, VolumeInventory.Current).Timed,
            Route.Protect => new ProtectRoute(site!, request.Trees, request.Listed, tuner).Timed,
            Route.Race => RaceRoute.On(
                VolumeMftSourceFactory.Default,
                request.Path,
                request.Racer,
                request.Walk,
                request.Drive,
                request.Table,
                buildYields,
                request.BeforePart).Timed,
            Route.Scan => AutoScanRoute.On(VolumeMftSourceFactory.Default, request.Path, tuner),
            Route.Plan => new PlanRoute(CleanupPlanner.CreateDefault()).Timed,
            _ => RouteRun.Blocking(ct => TableRoutes.Run(
                request.Route, VolumeMftSourceFactory.Default, request.Path, request.Table, opened: null, ct)),
        };

        for (var i = 1; i <= request.Runs; i++)
        {
            Console.Error.WriteLine($"Run {i} of {request.Runs}...");

            if (route.Before is { } before)
            {
                await before(cancel.Token);
            }

            runs.Add(await Timing.MeasureAsync(route.Run, cancel.Token));

            if (route.After is { } after)
            {
                await after();
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 3;
    }
    catch (Exception ex) when (site is not null && ex is IOException or UnauthorizedAccessException)
    {
        // Building a tree, the one thing here that writes. Its message names the file, so only the
        // error is repeated.
        Console.Error.WriteLine($"The tree could not be built: error 0x{ex.HResult:X8}.");
        return 2;
    }
    catch (Exception ex) when (store is not null && ex is IOException or UnauthorizedAccessException)
    {
        // Emptying or writing the route's own checksum store, or a file the search read, either of
        // which can fail as any file can. Its message names the file, so only the error is repeated.
        Console.Error.WriteLine($"The duplicate search or its checksum store could not read or write a file: error 0x{ex.HResult:X8}.");
        return 2;
    }
    catch (IOException ex)
    {
        // The table routes raise this when the volume goes away between runs. Its message names the
        // volume's state, never a path.
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (PartCommandFailedException ex)
    {
        // The parts after it would not have been measured as asked. Its message names no path.
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (InvalidOperationException ex) when (site is not null)
    {
        // A removal route found something gone beside what it removed, or the protect route a record
        // that does not match the folder it built. Its message names no path.
        Console.Error.WriteLine(ex.Message);
        return 4;
    }

    Console.Out.Write(Report.Render(
        request.Route,
        request.Route is Route.Race
            ? Tuning.DescribeRace(
                request.Racer,
                request.Walk,
                request.Drive,
                relation,
                request.Table,
                buildYields,
                request.BeforePart is not null)
            : request.Route is Route.Duplicates
                ? Tuning.DescribeDuplicates(request.Checksum, request.Cache)
                : Tuning.Describe(request.Route, request.Walk, request.Table, request.Snapshots, request.ReadBy, request.Tree, request.Trees),
        [.. request.Folders.Concat(request.Route is Route.Race ? [built] : []).Select(MeasuredPlace.Of).Distinct()],
        MachineFacts.Current,
        runs,
        census));
    return 0;
}
