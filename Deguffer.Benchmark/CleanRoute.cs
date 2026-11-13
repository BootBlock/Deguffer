using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Benchmark;

/// <summary>
/// Many small trees removed as one plan through <see cref="PlanExecutor"/>, as a clean of the
/// <c>obj</c> row removes one build folder per project: what each step costs around its removal.
///
/// <para><b>Each tree is a project.</b> A project folder holds a project file and a build folder, and
/// the plan removes the build folder and protects the project, so §5.6 is asked of every project after
/// every run. Each step carries the use check a build row carries, asked of the fake inspectors: the
/// route times what a step costs with nothing running, never what the processes on this machine
/// happen to cost to read.</para>
///
/// <para><b>The rest is the machine's own</b> where it only reads: whose folders no step may take, and
/// the volumes a folder is reachable at, because those are part of what a step costs on a real clean.
/// A refusal is recorded in the site rather than in the signed-in account's own record.</para>
/// </summary>
internal sealed class CleanRoute
{
    /// <summary>
    /// Each build folder's shape below its root: two levels, two folders to a folder, so six folders below it and seven with the files,
    /// as an <c>obj</c> folder holds its files in a handful.
    /// </summary>
    private const int TreeDepth = 2;

    private const int TreeFanout = 2;

    private readonly RemovalSite _site;
    private readonly TreeShape _shape;
    private readonly IReadOnlyList<string> _projects;
    private readonly PlanExecutor _executor;
    private CleanupPlan? _plan;

    /// <param name="shape">Each build folder's shape, as <see cref="Shape"/> gives it.</param>
    public CleanRoute(
        RemovalSite site,
        int trees,
        TreeShape shape,
        IUserEnvironment environment,
        ISystemDirectories system,
        IVolumeInventory volumes)
    {
        _site = site;
        _shape = shape;
        _projects = [.. Enumerable.Range(0, trees).Select(i => site.Place($"p{i}"))];
        _executor = new PlanExecutor(
            new FakeProcessRunner(),
            ParallelEnumerationScanner.Default,
            RefusalRecord.For(new FakeUserEnvironment(site.Place("account"))),
            inspector: FakeProcessInspector.NothingRunning,
            environment: environment,
            system: system,
            volumes: volumes);
    }

    public RouteRun Timed => RouteRun.Blocking(Run, Build, Check);

    /// <summary>The shape of each build folder holding <paramref name="files"/> files.</summary>
    public static TreeShape Shape(long files) => new(files, TreeDepth, TreeFanout);

    private static string BuildFolder(string project) => Path.Join(project, "obj");

    private static string ProjectFile(string project) => Path.Join(project, $"{Path.GetFileName(project)}.csproj");

    /// <summary>
    /// Build every build folder, and on the first run its project, then plan their removal as a
    /// provider would: one step per build folder, each with the question to ask again at the clean.
    /// </summary>
    private void Build(CancellationToken ct)
    {
        foreach (var project in _projects)
        {
            ct.ThrowIfCancellationRequested();

            if (_plan is null)
            {
                Directory.CreateDirectory(LongPath.Extended(project));
                File.WriteAllBytes(LongPath.Extended(ProjectFile(project)), []);
            }

            SynthesisedTree.Build(BuildFolder(project), _shape, ct);
        }

        var cleared = LiveTreeVeto.Apply(
            FakeLiveTreeInspector.NothingLive,
            [.. _projects.Select(project => new RecognisedBuildDirectory(BuildFolder(project), project))],
            lockFiles: [],
            ct).Cleared;

        _plan = new CleanupPlan
        {
            ProviderId = "benchmark",
            ProviderName = "Benchmark",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing uses these: the benchmark built them to remove.",
            Steps = [.. cleared.Select(folder => new DeleteDirectoryStep(folder.Path, "Build output") { UseCheck = folder.StillUnused })],
            ProtectedPaths =
            [
                .. _projects.Select(project => new ProtectedPath(project, "The project folder.", PathPresence.Present)),
                new ProtectedPath(_site.Beside, "The folder beside every tree.", PathPresence.Present),
            ],
        };
    }

    private RunTally Run(CancellationToken ct)
    {
        var workers = WorkerThreadPeak.Watch(WorkerThreads.Shared);
        CleanupResult result;
        int peakWorkers;

        try
        {
            result = _executor.ExecuteAsync(_plan!, runReach: null, residue: null, progress: null, underway: null, ct)
                .GetAwaiter().GetResult();
        }
        finally
        {
            peakWorkers = workers.Stop();
        }

        // The executor returns what it did when it is cancelled rather than throwing. A run stopped
        // part-way is not a result, so it is thrown here for the run loop to report.
        ct.ThrowIfCancellationRequested();

        if (result.Verification is not { Passed: true })
        {
            throw new InvalidOperationException("The clean's own §5.6 verification failed: something it protected is gone.");
        }

        return new RunTally(
            result.Steps.Count,
            BytesRead: 0,
            Complete: !result.Interrupted && result.Steps.Count == _projects.Count && result.Steps.All(step => step.Succeeded),
            PeakWorkers: peakWorkers,
            Passes: result.Steps.Aggregate((RemovalPasses?)null, (total, step) => RemovalPasses.Combine(total, step.Passes)),
            Steps: StepTimes.Of(result.Steps));
    }

    /// <summary>
    /// Every project and its project file still there, and the site's kept file. Asked here as well as
    /// by the clean's own verification, so a verification that stopped asking cannot pass a run.
    /// </summary>
    private void Check()
    {
        _site.ProveBesideStanding();

        if (_projects.Any(project => LongPath.ProbeFile(ProjectFile(project)) is not PathPresence.Present))
        {
            throw new InvalidOperationException("The clean took a project file beside the build folder it removed (§5.6).");
        }
    }
}
