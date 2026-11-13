using System.Runtime.InteropServices;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The result as it is pasted: the first run apart from the rest, and the later runs as a median
/// with their range.
/// </summary>
public sealed class ReportTests
{
    private const string Tuning = "4 folders listed at once, 64 KiB listing buffer";

    private static readonly MeasuredPlace Place = new("C:", DriveType.Fixed, "NTFS");

    private static readonly CollectorFacts Collector = new(Server: false, Concurrent: true, Gen0Size: null);

    private static readonly MachineFacts Machine =
        new(16, new Version(10, 0, 26100), new Version(10, 0, 0), Architecture.X64, Elevated: true, DebugBuild: false, Collector);

    [Fact]
    public void TheFirstRunIsReportedApartFromTheMedianOfTheRest()
    {
        var text = Render(Route.Walk, Run(10), Run(1), Run(3), Run(2));

        Assert.Equal("elapsed            10.000 s        2.000 s (1.000 s to 3.000 s)", Line(text, "elapsed"));
    }

    [Fact]
    public void OneRunHasNoLaterRuns()
    {
        var text = Render(Route.Walk, Run(0.25));

        Assert.Equal("elapsed            250.0 ms", Line(text, "elapsed"));
        Assert.DoesNotContain("later runs", text, StringComparison.Ordinal);
    }

    /// <summary>The walk has no bytes it can see, so it shows no rate rather than a rate of zero.</summary>
    [Fact]
    public void OnlyARouteThatReadsTheTableReportsBytesRead()
    {
        Assert.Contains("MiB/s read", Render(Route.Table, Run(2, bytes: 4L << 20)), StringComparison.Ordinal);
        Assert.DoesNotContain("MiB/s read", Render(Route.Walk, Run(2)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The walk says how many folders it listed at once, so a run below its thread count shows as
    /// one. The table lists no folders, so it shows no count rather than a count of zero.
    /// </summary>
    [Fact]
    public void OnlyTheWalkReportsTheFoldersItListedAtOnce()
    {
        var text = Render(Route.Walk, Run(1, peak: 16), Run(1, peak: 12), Run(1, peak: 14), Run(1, peak: 10));

        Assert.Equal("folders at once    16              12 (10 to 14)", Line(text, "folders at once"));
        Assert.DoesNotContain("folders at once", Render(Route.Table, Run(2)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A route that builds what Explore keeps on screen says what that keeps, which the peak working
    /// set cannot. A route that keeps nothing shows no figure rather than a figure of zero.
    /// </summary>
    [Fact]
    public void OnlyARouteThatKeepsWhatItBuiltReportsWhatItKeeps()
    {
        var text = Render(Route.ExploreFolder, Run(1, kept: 8L << 20), Run(1, kept: 2L << 20), Run(1, kept: 4L << 20));

        Assert.Equal("kept               8.0 MiB         3.0 MiB (2.0 MiB to 4.0 MiB)", Line(text, "kept"));
        Assert.Contains("kept ", Render(Route.Explore, Run(1)), StringComparison.Ordinal);
        Assert.Contains("kept ", Render(Route.ExploreWalk, Run(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("kept ", Render(Route.Table, Run(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("kept ", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void ARateIsTheItemsOverTheTime()
    {
        var text = Render(Route.Table, Run(2, items: 1_000_000, bytes: 1000L << 20));

        Assert.Equal("records/s          500,000", Line(text, "records/s"));
        Assert.Equal("MiB/s read         500.0", Line(text, "MiB/s read"));
    }

    /// <summary>
    /// A table route says how many reads it made, how many records the table holds in use, and what
    /// it read beyond them, which is what it read of free records for nothing.
    /// </summary>
    [Fact]
    public void ATableRouteReportsItsReadsAndTheFreeRecordsItRead()
    {
        var census = new TableCensus(RecordsInUse: 1_000, BytesPerRecord: 1024);
        var text = Render(census, Route.Table, Run(1, items: 3_048, reads: 40), Run(1, items: 2_024, reads: 60), Run(1, items: 1_000, reads: 80));

        Assert.Equal("records in use     1,000", Line(text, "records in use"));
        Assert.Equal("reads              40              70 (60 to 80)", Line(text, "reads"));
        Assert.Equal("free records read  2.0 MiB         0.5 MiB (0.0 MiB to 1.0 MiB)", Line(text, "free records read"));
    }

    /// <summary>
    /// A table route says how many reads of clusters it made and how long the attribute lists kept
    /// outside the table took to read, which is the time their depth decides.
    /// </summary>
    [Fact]
    public void ATableRouteReportsItsClusterReadsAndHowLongTheListsTook()
    {
        var text = Render(
            Route.Index,
            Run(1, clusterReads: 1_201, listSeconds: 0.25),
            Run(1, clusterReads: 1_201, listSeconds: 0.01),
            Run(1, clusterReads: 1_201, listSeconds: 0.03));

        Assert.Equal("cluster reads      1,201           1,201 (1,201 to 1,201)", Line(text, "cluster reads"));
        Assert.Equal("list reads took    250.0 ms        20.0 ms (10.0 ms to 30.0 ms)", Line(text, "list reads took"));
        Assert.DoesNotContain("list reads took", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the bitmap the table cannot say how many records are in use, so the report says so
    /// rather than calling every record read a free one. The walk reads no table and says neither.
    /// </summary>
    [Fact]
    public void ATableRouteWithoutACensusSaysTheRecordsInUseAreUnknown()
    {
        var text = Render(Route.Index, Run(1));

        Assert.Equal("records in use     unknown: the bitmap could not be read", Line(text, "records in use"));
        Assert.DoesNotContain("free records read", text, StringComparison.Ordinal);
        Assert.DoesNotContain("records in use", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("reads ", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    /// <summary>
    /// How many processors a run kept busy is its processor time over its elapsed time, so a run held
    /// up by the drive shows as one that used little of the machine.
    /// </summary>
    [Fact]
    public void ProcessorsBusyIsTheProcessorTimeOverTheElapsedTime()
    {
        var text = Render(
            Route.Table, Run(2, processorSeconds: 1), Run(2, processorSeconds: 6), Run(2, processorSeconds: 2), Run(4, processorSeconds: 1));

        Assert.Equal("processors busy    0.50            1.00 (0.25 to 3.00)", Line(text, "processors busy"));
    }

    /// <summary>A run that gave up is faster for the wrong reason, so it is counted where it shows.</summary>
    [Fact]
    public void ARunThatDidNotFinishIsCounted()
    {
        var text = Render(Route.Index, Run(1), Run(1, complete: false), Run(1));

        Assert.Equal("runs               3 (2 complete)", Line(text, "runs"));
    }

    [Fact]
    public void TheValuesTheRouteRanWithAreStated() =>
        Assert.Equal("tuning             " + Tuning, Line(Render(Route.Walk, Run(1)), "tuning"));

    /// <summary>
    /// The walk's values are the ones it was asked to run with, so two results taken at different
    /// values can never be read as one comparison.
    /// </summary>
    [Fact]
    public void TheWalksValuesAreTheOnesItRanWith() =>
        Assert.Equal(
            "3 folders listed at once, 8 KiB listing buffer",
            Deguffer.Benchmark.Tuning.Describe(Route.Walk, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots: true, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1));

    /// <summary>
    /// The Explore walk also states whether it took snapshots, which is the value it is timed both
    /// ways with, so the two results cannot be mistaken for each other.
    /// </summary>
    [Theory]
    [InlineData(true, "3 folders listed at once, 8 KiB listing buffer, snapshots on")]
    [InlineData(false, "3 folders listed at once, 8 KiB listing buffer, snapshots off")]
    public void TheExploreWalkSaysWhetherItTookSnapshots(bool snapshots, string expected) =>
        Assert.Equal(
            expected,
            Deguffer.Benchmark.Tuning.Describe(Route.ExploreWalk, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1));

    /// <summary>
    /// The Explore walk counts what its walk listed, as the walk does, so the two results name the
    /// same thing per second.
    /// </summary>
    [Fact]
    public void TheExploreWalkCountsEntries() =>
        Assert.Contains("entries/s", Render(Route.ExploreWalk, Run(1)), StringComparison.Ordinal);

    /// <summary>
    /// The same for a table route, whose values are the reads, the parse threads and the reads of the
    /// attribute lists kept outside the table.
    /// </summary>
    [Fact]
    public void ATableRoutesValuesAreTheOnesItRanWith() =>
        Assert.Equal(
            "2048 KiB reads, 8 in flight, parsed on 3 threads, 24 list reads in flight",
            Deguffer.Benchmark.Tuning.Describe(Route.Index, WalkTuning.Default, new TableTuning(2048 * 1024, 8, 3, 24), snapshots: true, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1));

    /// <summary>
    /// A race names the priority its build read at, because a result read at one priority says
    /// nothing about the other.
    /// </summary>
    [Theory]
    [InlineData(true, "1024 KiB reads at low priority while the walk runs,")]
    [InlineData(false, "1024 KiB reads at normal priority,")]
    public void ARaceNamesThePriorityItsBuildReadAt(bool yields, string reads) =>
        Assert.Contains(
            reads,
            Deguffer.Benchmark.Tuning.DescribeRace(
                Route.Sources, WalkTuning.Default, 'C', DiskRelation.SameDisk, new TableTuning(1024 * 1024, 8, 4, 32), yields, beforePart: false),
            StringComparison.Ordinal);

    [Fact]
    public void ADebugBuildIsCalledOut()
    {
        var text = Report.Render(Route.Walk, Tuning, [Place], Machine with { DebugBuild = true }, [Run(1)]);

        Assert.StartsWith("warning ", Line(text, "warning"), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    /// <summary>The folders route measures on every drive its folders are on, and names each.</summary>
    [Fact]
    public void SeveralPlacesAreEachNamed() =>
        Assert.Equal(
            "places             C: (Fixed, NTFS), D: (Fixed, NTFS)",
            Line(Report.Render(Route.Folders, Tuning, [Place, Place with { Root = "D:" }], Machine, [Run(1)]), "places"));

    /// <summary>The folders route runs with what a search would use on each drive, not with values the command line gave.</summary>
    [Fact]
    public void TheFoldersRouteSaysItRanWithASearchsValues() =>
        Assert.Equal(
            "the values a search uses for each drive's kind, under the shipped settings",
            Deguffer.Benchmark.Tuning.Describe(Route.Folders, new WalkTuning(3, 8 * 1024), TableTuning.Default, snapshots: true, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1));

    [Fact]
    public void ThePlaceIsTheDriveAndItsFileSystem() =>
        Assert.Equal("place              C: (Fixed, NTFS)", Line(Render(Route.Walk, Run(1)), "place"));

    /// <summary>
    /// A removal's passes are reported apart, each the first run apart from the median of the rest,
    /// with the processor time and the most worker threads the removal held.
    /// </summary>
    [Fact]
    public void ARemovalsPassesAreReportedApart()
    {
        var text = Render(Route.Remove, Removal(walk: 1, files: 4, folders: 2), Removal(walk: 0.5, files: 3, folders: 1), Removal(walk: 0.25, files: 5, folders: 3));

        Assert.Equal("walk               1.000 s         375.0 ms (250.0 ms to 500.0 ms)", Line(text, "walk"));
        Assert.Equal("file pass          4.000 s         4.000 s (3.000 s to 5.000 s)", Line(text, "file pass"));
        Assert.Equal("folder pass        2.000 s         2.000 s (1.000 s to 3.000 s)", Line(text, "folder pass"));
        Assert.Equal("processor time     6.000 s         6.000 s (6.000 s to 6.000 s)", Line(text, "processor time"));
        Assert.Equal("worker threads     12              12 (12 to 12)", Line(text, "worker threads"));
        Assert.DoesNotContain("folders at once", text, StringComparison.Ordinal);
        Assert.DoesNotContain("per step", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The clean route adds its steps' times, the time per step being the steps' over how many there
    /// were, so plans of different sizes read the same.
    /// </summary>
    [Fact]
    public void ACleansStepsAreReportedPerStepAndByPart()
    {
        var steps = new StepTimes(4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5), TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
        var text = Render(Route.Clean, Removal(walk: 0.25, files: 0.5, folders: 0.25, steps));

        Assert.Equal("per step           500.0 ms", Line(text, "per step"));
        Assert.Equal("use checks         500.0 ms", Line(text, "use checks"));
        Assert.Equal("looks              0.0 ms", Line(text, "looks"));
        Assert.Equal("acting             1.500 s", Line(text, "acting"));
        Assert.Equal("steps/s            4", Line(text, "steps/s"));
        Assert.Equal("file pass          500.0 ms", Line(text, "file pass"));
    }

    /// <summary>The shape of the tree is the whole of what a removal result is a result for, so it is stated.</summary>
    [Fact]
    public void ARemovalRouteStatesTheTreesItRemoved()
    {
        Assert.Equal(
            "a tree of 10 empty read-only files in 13 folders, 2 deep, 3 to a folder, removed as a clean removes it",
            Deguffer.Benchmark.Tuning.Describe(
                Route.Remove, WalkTuning.Default, TableTuning.Default, snapshots: true, LinkRead.Name, new TreeShape(10, 2, 3, ReadOnly: true), trees: 1));
        Assert.Equal(
            "1,000 build folders of 100 empty files in 7 folders, removed as one plan with nothing running",
            Deguffer.Benchmark.Tuning.Describe(
                Route.Clean, WalkTuning.Default, TableTuning.Default, snapshots: true, LinkRead.Name, CleanRoute.Shape(100), trees: 1000));
    }

    /// <summary>
    /// Where a duplicate search's time went: each part, indented as a part of the elapsed time, and what
    /// none covers. Then what each part kept and asked about, each stage of the comparison with what
    /// became of its reads, each lane, and the groups confirmed. The stages overlap, so each has its own
    /// time and they need not add up to the comparison's.
    /// </summary>
    [Fact]
    public void ADuplicateSearchReportsEachPartEachStageAndEachLane()
    {
        var text = Render(Route.Duplicates, Searched());

        Assert.Contains(
            string.Join(
                '\n',
                "  scan             2.000 s",
                "  walk             500.0 ms",
                "  identification   1.500 s",
                "  comparison       5.000 s",
                "  the rest         1.000 s",
                "files kept         10",
                "files identified   9",
                "files named        1",
                "first and last blocks",
                "  files            3",
                "  remembered       1",
                "  left out         1",
                "  read             1.0 MiB",
                "  took             3.000 s",
                "whole files",
                "  files            1",
                "  remembered       0",
                "  left out         0",
                "  read             3.0 MiB",
                "  took             2.000 s",
                "lane 1",
                "  readers          4",
                "  took             5.000 s",
                "  blocks read      3",
                "  whole read       1",
                "  read             4.0 MiB",
                "groups             2"),
            text,
            StringComparison.Ordinal);
        Assert.Equal("MiB/s read         0.4", Line(text, "MiB/s read"));
        Assert.DoesNotContain("folders at once", text, StringComparison.Ordinal);
    }

    /// <summary>The checksum and the cache change what a read costs, so both are stated.</summary>
    [Theory]
    [InlineData(false, "each run starting with no checksums kept")]
    [InlineData(true, "each run starting with the checksums an untimed search kept, loaded from their store")]
    public void TheDuplicatesRouteStatesItsChecksumAndItsCache(bool warm, string cache) =>
        Assert.Equal(
            "the values a search uses for each drive's kind, under the shipped settings, content compared by SHA-256, " + cache,
            Deguffer.Benchmark.Tuning.Describe(
                Route.Duplicates, WalkTuning.Default, TableTuning.Default, snapshots: true, LinkRead.Name, BenchmarkRequest.DefaultTree, trees: 1,
                ChecksumAlgorithm.Sha256, warm));

    /// <summary>
    /// A ten-second search: a scan of two seconds, a walk of half of one and identification of one and
    /// a half, then five seconds of comparison in one lane of four readers. Its first stage reads three
    /// files over three seconds, one of them remembered and one left out, and its second reads one
    /// whole file of 3 MiB over the last two.
    /// </summary>
    private static RunSample Searched()
    {
        var clock = new ManualTimeProvider();
        var stages = new DuplicateSearchTimings(clock);

        Part(DuplicateSearchPart.Scan, 2);
        Part(DuplicateSearchPart.Walk, 0.5);
        Part(DuplicateSearchPart.Identification, 1.5);
        stages.Kept = 10;
        stages.Identified = 9;
        stages.Named = 1;

        stages.Start(DuplicateSearchPart.Comparison);
        var lanes = stages.Comparing([4]);
        Read(ContentPart.Ends, new ContentReading(ContentReadResult.Read, default), 1 << 20, 1);
        Read(ContentPart.Ends, new ContentReading(ContentReadResult.Read, default, Remembered: true), 1 << 20, 1);
        Read(ContentPart.Ends, ContentReading.LeftOut(ContentReadResult.ReadFailed), 1 << 20, 1);
        Read(ContentPart.Whole, new ContentReading(ContentReadResult.Read, default), 3 << 20, 2);
        stages.End(DuplicateSearchPart.Comparison);

        var run = Run(10, bytes: 4L << 20);

        return run with { Tally = run.Tally with { Search = new SearchTimes(stages, Groups: 2) } };

        void Part(DuplicateSearchPart part, double seconds)
        {
            stages.Start(part);
            clock.Advance(TimeSpan.FromSeconds(seconds));
            stages.End(part);
        }

        void Read(ContentPart part, ContentReading reading, long bytes, double seconds)
        {
            var started = lanes.Now();
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lanes.Read(0, part, reading, bytes, started);
        }
    }

    private static RunSample Removal(double walk, double files, double folders, StepTimes? steps = null)
    {
        var run = Run(walk + files + folders, items: steps?.Count ?? 100, processorSeconds: 6);

        return run with
        {
            Tally = run.Tally with
            {
                PeakWorkers = 12,
                Passes = new RemovalPasses(
                    new PassTiming(TimeSpan.FromSeconds(walk), 10),
                    new PassTiming(TimeSpan.FromSeconds(files), 6),
                    new PassTiming(TimeSpan.FromSeconds(folders), 4)),
                Steps = steps,
            },
        };
    }

    private static string Render(Route route, params RunSample[] runs) => Report.Render(route, Tuning, [Place], Machine, runs);

    private static string Render(TableCensus census, Route route, params RunSample[] runs) =>
        Report.Render(route, Tuning, [Place], Machine, runs, census);

    /// <summary>
    /// Where a table route's time went, stage by stage, so a run held up by one stage on one thread
    /// reads apart from one held up by the drive. Indented, as parts of the elapsed time.
    /// </summary>
    [Fact]
    public void ATableRouteReportsEachStageItRanInOrder()
    {
        var text = Render(
            Route.Index,
            Staged(firstPass: 4, deferred: 0.5, links: 12, deferredCount: 4_051),
            Staged(firstPass: 2, deferred: 0.1, links: 12, deferredCount: 4_051),
            Staged(firstPass: 3, deferred: 0.3, links: 12, deferredCount: 4_051));

        string[] stages =
        [
            "  volume open", "  bitmap read", "  first pass", "  extension lists", "  deferred records", "  link placing", "  child links", "  the rest",
        ];
        var lines = text.Split('\n');
        Assert.Equal(stages, lines.Where(line => stages.Any(stage => line.StartsWith(stage + " ", StringComparison.Ordinal))).Select(line => line[..19].TrimEnd()));

        Assert.Equal("  first pass       4.000 s         2.500 s (2.000 s to 3.000 s)", Line(text, "  first pass"));
        Assert.Equal("  deferred records 500.0 ms        200.0 ms (100.0 ms to 300.0 ms)", Line(text, "  deferred records"));
        Assert.Equal("  volume open      500.0 ms        500.0 ms (500.0 ms to 500.0 ms)", Line(text, "  volume open"));
        Assert.Equal("  the rest         250.0 ms        250.0 ms (250.0 ms to 250.0 ms)", Line(text, "  the rest"));
        Assert.Equal("records deferred   4,051           4,051 (4,051 to 4,051)", Line(text, "records deferred"));
        Assert.Equal("links placed       12              12 (12 to 12)", Line(text, "links placed"));
    }

    /// <summary>
    /// Where the open's time went, step by step under the open, so a slow open reads as the handle,
    /// record 0 or the extension records. A step that read the volume says how many reads it made,
    /// and a step that read nothing has no count, rather than a count of zero.
    /// </summary>
    [Fact]
    public void ATableRouteReportsEachStepOfTheOpenUnderIt()
    {
        var text = Render(Route.Table, Opened(firstRecord: 0.5), Opened(firstRecord: 0.3), Opened(firstRecord: 0.4));

        var lines = text.Split('\n');
        var open = Array.FindIndex(lines, line => line.StartsWith("  volume open ", StringComparison.Ordinal));
        Assert.Equal(
            [
                "    handle         2.0 ms          2.0 ms (2.0 ms to 2.0 ms)",
                "    boot sector    0.0 ms          0.0 ms (0.0 ms to 0.0 ms)",
                "      reads        1               1 (1 to 1)",
                "    record 0       500.0 ms        350.0 ms (300.0 ms to 400.0 ms)",
                "      reads        1               1 (1 to 1)",
                "    list           0.0 ms          0.0 ms (0.0 ms to 0.0 ms)",
                "    extensions     0.0 ms          0.0 ms (0.0 ms to 0.0 ms)",
                "      reads        40              40 (40 to 40)",
                "    bitmap pieces  0.0 ms          0.0 ms (0.0 ms to 0.0 ms)",
                "  bitmap read      1.0 ms          1.0 ms (1.0 ms to 1.0 ms)",
            ],
            lines[(open + 1)..(open + 11)]);
    }

    /// <summary>
    /// A run whose open is timed step by step by a clock this test moves: the handle two
    /// milliseconds, record 0 as given, forty reads of extension records that take no time, and
    /// every other step nothing.
    /// </summary>
    private static RunSample Opened(double firstRecord)
    {
        var clock = new ManualTimeProvider();
        var open = new MftOpenTimings(clock);

        open.Start();
        clock.Advance(TimeSpan.FromMilliseconds(2));
        open.End(MftOpenStep.VolumeHandle);
        open.Read();
        open.End(MftOpenStep.BootSector);
        clock.Advance(TimeSpan.FromSeconds(firstRecord));
        open.Read();
        open.End(MftOpenStep.FirstRecord);
        open.End(MftOpenStep.AttributeList);

        for (var i = 0; i < 40; i++)
        {
            open.Read();
        }

        open.End(MftOpenStep.ExtensionRecords);
        open.End(MftOpenStep.BitmapPlacement);

        var run = Staged(firstPass: 1, deferred: 0, links: 0, deferredCount: 0, linkStages: false);
        return run with { Tally = run.Tally with { Open = open } };
    }

    /// <summary>The table route places no links, so it shows no count of them rather than a count of zero.</summary>
    [Fact]
    public void OnlyARouteThatPlacesLinksReportsHowManyItPlaced()
    {
        var text = Render(Route.Table, Staged(firstPass: 1, deferred: 0, links: 0, deferredCount: 0, linkStages: false));

        Assert.Contains("records deferred", text, StringComparison.Ordinal);
        Assert.DoesNotContain("links placed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("child links", text, StringComparison.Ordinal);
    }

    /// <summary>The mean read, bytes of records over reads of records, which a table in many pieces makes small.</summary>
    [Fact]
    public void ATableRouteReportsItsMeanRead()
    {
        var text = Render(Route.Table, Run(1) with { Tally = Run(1).Tally with { MeanReadBytes = 96 * 1024 } });

        Assert.Equal("mean read          96.0 KiB", Line(text, "mean read"));
        Assert.DoesNotContain("mean read", Render(Route.Walk, Run(1)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run whose stages are timed by a clock this test moves: the open half a second, the bitmap a
    /// millisecond, the first pass and the deferred records as given, every other stage nothing, and
    /// a quarter of a second outside them all.
    /// </summary>
    private static RunSample Staged(double firstPass, double deferred, int links, int deferredCount, bool linkStages = true)
    {
        var clock = new ManualTimeProvider();
        var timings = new MftStageTimings(clock) { DeferredRecords = deferredCount, LinksPlaced = links };

        timings.Start();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        timings.End(MftBuildStage.BitmapRead);
        clock.Advance(TimeSpan.FromSeconds(firstPass));
        timings.End(MftBuildStage.FirstPass);
        timings.End(MftBuildStage.ExtensionLists);
        clock.Advance(TimeSpan.FromSeconds(deferred));
        timings.End(MftBuildStage.DeferredRecords);

        if (linkStages)
        {
            timings.End(MftBuildStage.LinkPlacement);
            timings.End(MftBuildStage.ChildLinks);
        }

        var run = Run(0.5 + 0.001 + firstPass + deferred + 0.25);
        return run with { Tally = run.Tally with { Stages = timings, OpenTime = TimeSpan.FromSeconds(0.5) } };
    }

    /// <summary>
    /// A race reports each part of its rounds and how many times as long each took raced, and what
    /// the collector did while each walk ran. A round is several parts, so it has no rate, and the race
    /// lists no folders of its own beside the walk's count.
    /// </summary>
    [Fact]
    public void ARaceReportsEachPartAndHowMuchTheOtherSlowedIt()
    {
        var text = Render(
            Route.Race,
            Raced(walkAlone: 2, walkRaced: 5, buildAlone: 4, buildRaced: 5, pausedRaced: 1.5),
            Raced(walkAlone: 1, walkRaced: 2, buildAlone: 3, buildRaced: 3.3, pausedRaced: 0.25),
            Raced(walkAlone: 1, walkRaced: 3, buildAlone: 3, buildRaced: 3.6, pausedRaced: 0.75),
            Raced(walkAlone: 1, walkRaced: 4, buildAlone: 3, buildRaced: 3.9, pausedRaced: 0.5));

        Assert.Equal("walk alone         2.000 s         1.000 s (1.000 s to 1.000 s)", Line(text, "walk alone"));
        Assert.Equal("walk raced         5.000 s         3.000 s (2.000 s to 4.000 s)", Line(text, "walk raced"));
        Assert.Equal("build raced        5.000 s         3.600 s (3.300 s to 3.900 s)", Line(text, "build raced"));

        var slowdowns = text.Split('\n').Where(line => line.StartsWith("  times as long ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            ["  times as long    2.50x           3.00x (2.00x to 4.00x)", "  times as long    1.25x           1.20x (1.10x to 1.30x)"],
            slowdowns);

        var paused = text.Split('\n').Where(line => line.StartsWith("  paused ", StringComparison.Ordinal)).ToArray();
        Assert.Equal("  paused           1.500 s         500.0 ms (250.0 ms to 750.0 ms)", paused[1]);

        Assert.Equal("candidates         100             100 (100 to 100)", Line(text, "candidates"));
        Assert.DoesNotContain("elapsed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("folders at once", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MiB/s read", text, StringComparison.Ordinal);
        Assert.DoesNotContain("full collections", text, StringComparison.Ordinal);
        Assert.DoesNotContain("processors busy", text, StringComparison.Ordinal);
    }

    /// <summary>Two results are comparable only under the same collector, so every result names it.</summary>
    [Fact]
    public void EveryResultNamesItsCollector()
    {
        Assert.Equal("collector          workstation, background collections", Line(Render(Route.Walk, Run(1)), "collector"));
        Assert.Equal(
            "collector          server, no background collections, generation 0 budget 0x4000000",
            Line(Report.Render(Route.Walk, Tuning, [Place], Machine with { Collector = new(true, false, "0x4000000") }, [Run(1)]), "collector"));
    }

    private static RunSample Raced(double walkAlone, double walkRaced, double buildAlone, double buildRaced, double pausedRaced)
    {
        static RacePart Part(double seconds, double paused = 0) =>
            new(TimeSpan.FromSeconds(seconds), new HeapCounters(TimeSpan.FromSeconds(paused), 12, 3, 1, 64 << 20), 100, Complete: true);

        var race = new RaceTimes(
            Route.Sources, Part(walkAlone), Part(buildAlone), Part(walkRaced, pausedRaced), Part(buildRaced), Reversed: false);
        var run = Run(walkAlone + walkRaced + buildAlone);

        return run with { Tally = run.Tally with { Race = race } };
    }

    private static RunSample Run(
        double seconds,
        long items = 100,
        long bytes = 0,
        bool complete = true,
        int peak = 0,
        long kept = 0,
        double processorSeconds = 0,
        long reads = 0,
        long clusterReads = 0,
        double listSeconds = 0) =>
        new(
            TimeSpan.FromSeconds(seconds),
            new RunTally(
                items, bytes, complete, peak, Reads: reads, ClusterReads: clusterReads, ListReadTime: TimeSpan.FromSeconds(listSeconds)),
            AllocatedBytes: 1 << 20,
            FullCollections: 0,
            PeakWorkingSet: 64 << 20,
            TimeSpan.FromSeconds(processorSeconds),
            kept);

    private static string Line(string text, string label) =>
        text.Split('\n').Single(line => line.StartsWith(label + " ", StringComparison.Ordinal));
}
