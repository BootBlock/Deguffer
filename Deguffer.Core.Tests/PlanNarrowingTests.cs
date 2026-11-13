using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The plans Clean executes, narrowed to the ticked steps with what each declined subject held asked of
/// the disk: the same protection, subject for subject and in the same order, as asking each alone
/// gives, and asked with the walks overlapping across every row, so a row of thousands of unticked
/// items does not pay each walk in turn.
///
/// <para>The tests of overlap and order use invented paths on a share of their own and a content
/// question of the test's own, so no walk another test runs shares their volume's slots. Each wait is
/// bounded, so a narrowing that walks one subject at a time fails on its assertion rather than
/// hanging.</para>
/// </summary>
public sealed class PlanNarrowingTests : IDisposable
{
    private const string Reason = "Left alone because it was not selected for this run.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Every shape a declined subject takes is protected as asking it alone protects it: a folder
    /// holding a file, one holding only empty folders, an empty one, a path that is not there, and one
    /// Windows will not list. Each was there when the plan was made, so each is recorded present
    /// without a probe, and the chosen steps, the plans' own protections and the folders on disk are
    /// left as they were (§5.6).
    /// </summary>
    [Fact]
    public async Task EachDeclinedSubjectIsProtectedAsAskingItAloneProtectsIt()
    {
        var holding = _temp.CreateDirectory("holding");
        _temp.CreateFile(8, "holding", "deep", "one.bin");
        var skeleton = _temp.CreateDirectory("skeleton");
        _temp.CreateDirectory("skeleton", "a", "b");
        var empty = _temp.CreateDirectory("empty");
        var absent = Path.Join(_temp.Path, "never-existed");
        var refused = _temp.CreateDirectory("holder", "refused");
        var chosenFirst = Step(_temp.CreateDirectory("chosen-first"));
        var chosenSecond = Step(_temp.CreateDirectory("chosen-second"));
        var toolRoot = new ProtectedPath(_temp.Path, "The tool's root.", PathPresence.Present, HeldContentBefore: true);

        var first = PlanOf([Step(holding), chosenFirst, Step(skeleton), Step(empty)], toolRoot);
        var second = PlanOf([Step(absent), chosenSecond, Step(refused)]);

        IReadOnlyList<CleanupPlan> narrowed;
        List<ProtectedPath> alone;

        using (DeniedDirectory.WithUnreadableAttributes(refused))
        {
            narrowed = await new PlanNarrowing(ScanTuner.Shipped).NarrowAsync(
                [new ChosenSteps(first, [chosenFirst]), new ChosenSteps(second, [chosenSecond])],
                default);

            string[] declined = [holding, skeleton, empty, absent, refused];
            alone = [.. declined.Select(path => new ProtectedPath(path, Reason, PathPresence.Present, DirectoryContent.IsPresent(path)))];
        }

        Assert.Equal([chosenFirst], narrowed[0].Steps);
        Assert.Equal([toolRoot, .. alone[..3]], narrowed[0].ProtectedPaths);
        Assert.Equal([chosenSecond], narrowed[1].Steps);
        Assert.Equal(alone[3..], narrowed[1].ProtectedPaths);

        // The answers the comparison rests on, so a question that answered every subject alike could not pass it.
        Assert.Equal(
            [true, false, false, false, false],
            narrowed.SelectMany(plan => plan.ProtectedPaths).Where(path => path != toolRoot).Select(path => path.HeldContentBefore));

        Assert.All([holding, skeleton, empty, refused, chosenFirst.Path, chosenSecond.Path], path => Assert.True(Directory.Exists(path), path));
    }

    /// <summary>
    /// The walks of different rows overlap: a subject whose walk waits until a second row's subject is
    /// being walked still gets its answer. Walked one row after another, or one subject after another,
    /// the first would wait for a second that never starts.
    /// </summary>
    [Fact]
    public async Task TheWalksOfEveryRowOverlap()
    {
        using var both = new Barrier(2);
        var first = PlanOf([Step(Subject(0)), Step(Subject(1))]);
        var second = PlanOf([Step(Subject(2)), Step(Subject(3))]);

        var narrowed = await new PlanNarrowing(TunedTo(2), _ => both.SignalAndWait(Patience)).NarrowAsync(
            [new ChosenSteps(first, [first.Steps[1]]), new ChosenSteps(second, [second.Steps[1]])],
            default);

        Assert.All(narrowed.SelectMany(plan => plan.ProtectedPaths), path => Assert.True(path.HeldContentBefore, path.Path));
    }

    /// <summary>
    /// Each protection carries its own subject's answer, in the order the plans give their steps,
    /// though each walk finishes only after the one given after it has. Paired by position, a
    /// protection out of place would say one folder held content on the evidence of another.
    /// </summary>
    [Fact]
    public async Task EachProtectionCarriesItsOwnSubjectsAnswerWhenTheWalksFinishInReverse()
    {
        const int Count = 6;

        string[] subjects = [.. Enumerable.Range(0, Count).Select(Subject)];
        var finished = Enumerable.Range(0, Count).Select(_ => new ManualResetEventSlim()).ToArray();
        var first = PlanOf([.. subjects[..3].Select(Step), Step(Subject(100))]);
        var second = PlanOf([.. subjects[3..].Select(Step), Step(Subject(101))]);

        try
        {
            var narrowed = await new PlanNarrowing(
                TunedTo(Count),
                path =>
                {
                    var index = Array.IndexOf(subjects, path);
                    var waited = index == Count - 1 || finished[index + 1].Wait(Patience);

                    finished[index].Set();

                    // Content on the even subjects only, so a protection paired with its neighbour's answer differs.
                    return waited && index % 2 == 0;
                }).NarrowAsync([new ChosenSteps(first, [first.Steps[3]]), new ChosenSteps(second, [second.Steps[3]])], default);

            var protections = narrowed.SelectMany(plan => plan.ProtectedPaths).ToList();

            Assert.Equal(subjects, protections.Select(path => path.Path));
            Assert.Equal(Enumerable.Range(0, Count).Select(index => index % 2 == 0), protections.Select(path => path.HeldContentBefore));
        }
        finally
        {
            foreach (var signal in finished)
            {
                signal.Dispose();
            }
        }
    }

    /// <summary>A plan whose every step was chosen is handed back as it was, and nothing is walked for it.</summary>
    [Fact]
    public async Task APlanWhoseEveryStepWasChosenIsHandedBackAndNothingIsWalked()
    {
        var plan = PlanOf([Step(Subject(0)), Step(Subject(1))]);

        var narrowed = await new PlanNarrowing(TunedTo(2), NeverWalked).NarrowAsync([new ChosenSteps(plan, plan.Steps)], default);

        Assert.Same(plan, Assert.Single(narrowed));
    }

    [Fact]
    public async Task ACancelledNarrowingThrowsRatherThanReturningAPlanThatWasNotAsked()
    {
        var plan = PlanOf([Step(Subject(0)), Step(Subject(1))]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlanNarrowing(TunedTo(2), NeverWalked).NarrowAsync([new ChosenSteps(plan, [])], new CancellationToken(canceled: true)));
    }

    private static bool NeverWalked(string path) =>
        throw new InvalidOperationException($"A subject was walked that no narrowing needed: {path}");

    private static CleanupPlan PlanOf(IReadOnlyList<CleanupStep> steps, params IReadOnlyList<ProtectedPath> protectedPaths) => new()
    {
        ProviderId = "example",
        ProviderName = "Example",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
        ProtectedPaths = protectedPaths,
    };

    private static DeleteDirectoryStep Step(string path) => new(path, Path.GetFileName(path));

    private static string Subject(int index) => $@"\\volume.test\example\item-{index}";

    /// <summary>A walk of <paramref name="threads"/> on every volume: the tuner knows no drive's kind, so each is unknown.</summary>
    private static ScanTuner TunedTo(int threads) => new(new FakePreferences(AppPreferences.Default with
    {
        Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: threads)),
    }));
}
