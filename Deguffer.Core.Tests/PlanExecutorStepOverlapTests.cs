using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A plan whose steps remove folders apart from each other runs them at once, so a row of a few hundred
/// small build folders does not pay each folder's fixed costs one after another. What a clean promises
/// stays as it was: a folder inside another is never removed beside it, the deletions of every step
/// share one bound, each step's use check is asked immediately before its own removal, and a cancelled
/// clean reports every step it started and is still verified (§5.6).
///
/// <para>Each test proves what ran together by the order things happened in, never by timing alone: a
/// removal that waits for another's first deletion can see it only if both are under way.</para>
/// </summary>
public sealed class PlanExecutorStepOverlapTests : IDisposable
{
    private const int FilesPerFolder = 100;

    /// <summary>Far longer than a removal takes to start, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RefusalRecord RefusalLog => RefusalRecord.For(new FakeUserEnvironment(_temp.Path));

    /// <summary>
    /// Each removal's first deletion waits for the other's. One at a time, the first would wait for a
    /// removal that cannot start until it ends.
    /// </summary>
    [Fact]
    public async Task TwoFoldersApartInOnePlanAreRemovedAtOnce()
    {
        var root = _temp.CreateDirectory("src");
        var first = Tree("src", "app", "obj");
        var second = Tree("src", "lib", "obj");
        var sibling = _temp.CreateFile(64, "src", "app", "app.csproj");

        using var firstArrived = new ManualResetEventSlim();
        using var secondArrived = new ManualResetEventSlim();
        var firstSawSecond = false;
        var secondSawFirst = false;

        var fs = new FirstDeletionHooks(WindowsFileSystem.Default, new Dictionary<string, Action>
        {
            [first] = () =>
            {
                firstArrived.Set();
                firstSawSecond = secondArrived.Wait(Patience);
            },
            [second] = () =>
            {
                secondArrived.Set();
                secondSawFirst = firstArrived.Wait(Patience);
            },
        });

        CleanupStep[] steps = [new DeleteDirectoryStep(first, "app"), new DeleteDirectoryStep(second, "lib")];

        var result = await Executor(fs).ExecuteAsync(
            Plan(
                steps,
                new ProtectedPath(root, "The source folder.", PathPresence.Present),
                new ProtectedPath(sibling, "The project beside its output.", PathPresence.Present)),
            runReach: null,
            residue: null,
            progress: null,
            underway: null,
            CancellationToken.None);

        Assert.True(firstSawSecond, "The first folder was removed alone: the second had not started.");
        Assert.True(secondSawFirst, "The second folder was removed alone: the first had not started.");

        Assert.Equal(steps.Select(step => step.Description), result.Steps.Select(step => step.Description));
        Assert.All(result.Steps, step => Assert.True(step.Succeeded, step.Message));
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));

        // §5.6's negative, on the disk and in the verdict.
        Assert.True(File.Exists(sibling));
        Assert.True(result.Verification!.Passed);
        Assert.All(result.Verification.Checks, check => Assert.Equal(VerificationOutcome.Survived, check.Outcome));
    }

    /// <summary>
    /// The inner folder comes first in the plan and its first deletion is held, which is when a removal
    /// of the folder holding it would run if it were let start beside it. Every deletion of the inner
    /// folder still begins before any deletion of the rest of the outer one.
    /// </summary>
    [Fact]
    public async Task AFolderInsideAnotherIsNeverRemovedBesideIt()
    {
        var outer = _temp.CreateDirectory("out");
        var inner = Tree("out", "in");
        var rest = Tree("out", "rest");

        var fs = new FirstDeletionHooks(WindowsFileSystem.Default, new Dictionary<string, Action>
        {
            [inner] = () => Thread.Sleep(TimeSpan.FromMilliseconds(500)),
        });

        var result = await Executor(fs).ExecuteAsync(
            Plan([new DeleteDirectoryStep(inner, "inner"), new DeleteDirectoryStep(outer, "outer")]),
            runReach: null,
            residue: null,
            progress: null,
            underway: null,
            CancellationToken.None);

        var order = fs.Deleted.ToList();
        var lastOfInner = order.FindLastIndex(path => LongPath.Contains(inner, path));
        var firstOfRest = order.FindIndex(path => LongPath.Contains(rest, path));

        Assert.InRange(lastOfInner, 0, firstOfRest - 1);
        Assert.All(result.Steps, step => Assert.True(step.Succeeded, step.Message));
        Assert.False(Directory.Exists(outer));
    }

    /// <summary>
    /// Several removals at once share the one removal's bound rather than each taking it, so the disk's
    /// queue is no deeper than a single removal makes it (G4). More than one deletion at a time is the
    /// floor, because the removals' crews are still parallel inside themselves.
    /// </summary>
    [Fact]
    public async Task TheStepsOfAPlanShareOneBoundOnTheirDeletions()
    {
        var trees = Enumerable.Range(0, 6).Select(index => Tree("src", $"p{index}", "obj")).ToList();
        var watched = new WatchedDeletions(WindowsFileSystem.Default, TimeSpan.FromMilliseconds(5));

        var result = await Executor(watched).ExecuteAsync(
            Plan([.. trees.Select(tree => new DeleteDirectoryStep(tree, tree))]),
            runReach: null,
            residue: null,
            progress: null,
            underway: null,
            CancellationToken.None);

        Assert.All(result.Steps, step => Assert.True(step.Succeeded, step.Message));
        Assert.All(trees, tree => Assert.False(Directory.Exists(tree)));
        Assert.InRange(watched.MostAtOnce, 2, DirectoryRemover.MostAtOnce);
    }

    /// <summary>
    /// A step's use check is asked immediately before its own removal, while the step beside it is
    /// already deleting, and what it finds in use then holds the step back. §5.6 then proves the held
    /// folder standing with everything the plan protected.
    /// </summary>
    [Fact]
    public async Task AUseCheckAskedWhileAnotherStepDeletesStillHoldsItsOwnStepBack()
    {
        var first = Tree("src", "app", "obj");
        var held = Tree("src", "lib", "obj");
        var heldFiles = Directory.EnumerateFiles(held, "*", SearchOption.AllDirectories).Count();

        using var firstDeleting = new ManualResetEventSlim();
        var check = new BuildStartsOnceTheOtherDeletes(firstDeleting);

        var fs = new FirstDeletionHooks(WindowsFileSystem.Default, new Dictionary<string, Action>
        {
            [first] = firstDeleting.Set,
        });

        var result = await Executor(fs).ExecuteAsync(
            Plan([new DeleteDirectoryStep(first, "app"), new DeleteDirectoryStep(held, "lib") { UseCheck = check }]),
            runReach: null,
            residue: null,
            progress: null,
            underway: null,
            CancellationToken.None);

        Assert.True(check.AskedWhileTheOtherDeleted, "The use check was asked before the other step began deleting.");

        var heldBack = result.Steps[1];
        Assert.False(heldBack.Succeeded);
        Assert.Equal("Nothing was removed: a build is writing to it.", heldBack.Message);
        Assert.Equal(heldFiles, Directory.EnumerateFiles(held, "*", SearchOption.AllDirectories).Count());
        Assert.False(Directory.Exists(first));

        var survivor = Assert.Single(result.Verification!.Checks);
        Assert.Equal(held, survivor.Subject);
        Assert.Equal(VerificationOutcome.Survived, survivor.Outcome);
        Assert.True(result.Verification.Passed);
    }

    /// <summary>
    /// The clean is cancelled once three removals are each under way. Each is reported as stopped with
    /// what it had done, the step after them never starts, and the tool's own folder and its settings
    /// beside the targets are proved standing.
    /// </summary>
    [Fact]
    public async Task ACleanCancelledWithThreeStepsUnderWayReportsEachAsStoppedAndIsStillVerified()
    {
        var root = _temp.CreateDirectory("tool");
        var settings = _temp.CreateFile(64, "tool", "settings.json");
        var trees = new[] { Tree("tool", "a"), Tree("tool", "b"), Tree("tool", "c") };

        using var cts = new CancellationTokenSource();
        using var allUnderWay = new CountdownEvent(trees.Length);

        void CancelOnceAllAreUnderWay()
        {
            allUnderWay.Signal();

            if (allUnderWay.Wait(Patience))
            {
                cts.Cancel();
            }
        }

        var fs = new FirstDeletionHooks(WindowsFileSystem.Default, trees.ToDictionary(tree => tree, _ => (Action)CancelOnceAllAreUnderWay));
        var runner = new FakeProcessRunner();
        var removals = trees.Select(tree => new DeleteDirectoryStep(tree, "A tree")).ToList();

        var result = await new PlanExecutor(runner, ParallelEnumerationScanner.Default, RefusalLog, environment: Environment(), fileSystem: fs)
            .ExecuteAsync(
                Plan(
                    [.. removals, new RunCommandStep("tool", "prune", "Prune") { WhenCancelled = CancelledCommand.Ended }],
                    new ProtectedPath(root, "The tool's own folder.", PathPresence.Present),
                    new ProtectedPath(settings, "The tool's settings.", PathPresence.Present)),
                runReach: null,
                residue: null,
                progress: null,
                underway: null,
                cts.Token);

        Assert.True(result.Interrupted);
        Assert.Equal(removals.Select(step => step.Description), result.Steps.Select(step => step.Description));
        Assert.All(result.Steps, step =>
        {
            Assert.True(step.Interrupted, step.Message);
            Assert.StartsWith("Stopped", step.Message);
        });
        Assert.Empty(runner.Invocations);

        // §5.6's negative, on the disk and in the verdict.
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(settings));
        Assert.True(result.Verification!.Passed);
        Assert.Equal(
            [VerificationOutcome.Survived, VerificationOutcome.Survived],
            result.Verification.Checks.Select(check => check.Outcome));
    }

    private PlanExecutor Executor(IFileSystem fs) =>
        new(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog, environment: Environment(), fileSystem: fs);

    private FakeUserEnvironment Environment() => new(_temp.Path);

    /// <summary>A tree of four folders, each holding <see cref="FilesPerFolder"/> small files.</summary>
    private string Tree(params string[] segments)
    {
        var root = _temp.CreateDirectory(segments);

        for (var folder = 0; folder < 4; folder++)
        {
            for (var i = 0; i < FilesPerFolder; i++)
            {
                _temp.CreateFile(16, [.. segments, $"d{folder}", $"f{i}.bin"]);
            }
        }

        return root;
    }

    private static CleanupPlan Plan(IReadOnlyList<CleanupStep> steps, params ProtectedPath[] protectedPaths) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
        ProtectedPaths = protectedPaths,
    };

    /// <summary>
    /// A build that starts writing to its folder once the other step has begun deleting, which the
    /// check sees only if it is asked then rather than when the plan began.
    /// </summary>
    private sealed class BuildStartsOnceTheOtherDeletes(ManualResetEventSlim otherDeleting) : IUseCheck
    {
        public bool AskedWhileTheOtherDeleted { get; private set; }

        public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
        {
            AskedWhileTheOtherDeleted = otherDeleting.Wait(Patience, ct);
            return AskedWhileTheOtherDeleted ? [new InUseNow(step.Path, "a build is writing to it")] : [];
        }
    }
}
