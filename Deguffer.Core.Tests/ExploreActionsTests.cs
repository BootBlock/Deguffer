using Deguffer.Core.Diagnostics;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What Explore says about a path while its policy is being built, and how a removal waits for that
/// policy before anything is deleted.
///
/// <para>The policy is built in the background, because building it runs every provider's probes.
/// That is a window in which the page is open and the user can select things, and each rule here is
/// about that window: nothing is allowed until the whole policy is known, a failed build refuses
/// rather than allows, and the deleting path never reads a half-built answer.</para>
/// </summary>
public sealed class ExploreActionsTests : IDisposable
{
    private const string Reason = "Kept by a test.";

    private readonly TempDirectory _temp = new();
    private readonly CrashLog _faults;

    public ExploreActionsTests() => _faults = new CrashLog(new FakeUserEnvironment(_temp.Path));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WhileThePolicyIsBuildingEveryPathIsRefusedWithASentenceSayingSo()
    {
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        var actions = Actions(_ => building.Task);

        actions.Prepare();

        var verdict = actions.Verdict(_temp.CreateFile(8, "a.bin"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("still working out what it has to protect", verdict.Reason);
    }

    /// <summary>
    /// A failed build says so rather than "in a moment", and still refuses: without the probed half
    /// of §5.2 there is no knowing what else would have been refused.
    /// </summary>
    [Fact]
    public void AFailedBuildRefusesEveryPathUntilABuildSucceeds()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var attempts = 0;

        var actions = Actions(_ => ++attempts == 1
            ? Task.FromException<ExploreActionPolicy>(new IOException("a probe failed"))
            : Task.FromResult(Policy()));

        actions.Prepare();

        var failed = actions.Verdict(file);

        Assert.False(failed.IsAllowed);
        Assert.Contains("could not work out what it has to protect", failed.Reason);

        actions.Prepare();

        Assert.Equal(2, attempts);
        Assert.True(actions.Verdict(file).IsAllowed);
    }

    /// <summary>
    /// A factory that throws while constructing its providers, before it has a task to return, is a
    /// failed build. Thrown out of <see cref="ExploreActions.Prepare"/> it would take down the
    /// constructor of the page that called it.
    /// </summary>
    [Fact]
    public void AFactoryThatThrowsBeforeItReturnsIsAFailedBuild()
    {
        var actions = Actions(_ => throw new InvalidOperationException("a provider would not construct"));

        actions.Prepare();

        Assert.Contains("could not work out what it has to protect", actions.Verdict(_temp.Path).Reason);
    }

    /// <summary>
    /// A policy that is built, or on its way, is kept. Each build runs every provider's probes, so a
    /// return visit to the page must not start another.
    /// </summary>
    [Fact]
    public void PreparingAgainKeepsAPolicyThatIsBuiltOrOnItsWay()
    {
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        var attempts = 0;
        var actions = Actions(_ =>
        {
            attempts++;
            return building.Task;
        });

        actions.Prepare();
        actions.Prepare();

        building.SetResult(Policy());
        actions.Prepare();

        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// Ready is raised once the build's task has completed, never from inside it. A handler raised
    /// earlier asks for a verdict, is told the policy is still being built, and is never told
    /// otherwise.
    /// </summary>
    [Fact]
    public void ReadyArrivesOnceTheAnswerCanBeRead() => WithoutContext(() =>
    {
        var file = _temp.CreateFile(8, "a.bin");
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        var actions = Actions(_ => building.Task);
        var heard = new List<bool>();

        actions.Ready += (_, _) => heard.Add(actions.Verdict(file).IsAllowed);
        actions.Prepare();

        Assert.Empty(heard);

        building.SetResult(Policy());

        Assert.Equal([true], heard);
    });

    /// <summary>
    /// A build replaced while it ran has nothing to tell a page that is now waiting on its successor.
    /// Announcing it would restate every note from a policy the page no longer asks.
    /// </summary>
    [Fact]
    public void ABuildReplacedWhileItRanAnnouncesNothing() => WithoutContext(() =>
    {
        var first = new TaskCompletionSource<ExploreActionPolicy>();
        var second = new TaskCompletionSource<ExploreActionPolicy>();
        var builds = new Queue<TaskCompletionSource<ExploreActionPolicy>>([first, second]);
        var actions = Actions(_ => builds.Dequeue().Task);
        var announced = 0;

        actions.Ready += (_, _) => announced++;
        actions.Prepare();
        actions.Reconsider();

        first.SetResult(Policy());

        Assert.Equal(0, announced);

        second.SetResult(Policy());

        Assert.Equal(1, announced);
    });

    /// <summary>
    /// The page can say only that a build failed. The log is where it says which probe, so the
    /// failure is written there as well as reaching the page as a refusal.
    /// </summary>
    [Fact]
    public void AFailedBuildIsWrittenToTheFaultLog()
    {
        var actions = Actions(_ => Task.FromException<ExploreActionPolicy>(new IOException("a probe failed")));

        actions.Prepare();

        var written = File.ReadAllText(_faults.FilePath);

        Assert.Contains("Building Explore's removal policy", written);
        Assert.Contains("a probe failed", written);
    }

    /// <summary>
    /// The deleting path waits for the whole policy. A removal decided against a half-built one is
    /// the one thing building it in the background must never buy.
    /// </summary>
    [Fact]
    public async Task ARemovalWaitsForThePolicyBeforeAskingOrRemoving()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => building.Task, prompt);

        actions.Prepare();

        var removing = actions.RemoveAsync([Item(file)], ExploreRemovalMode.RecycleBin);

        Assert.False(removing.IsCompleted);
        Assert.Empty(prompt.Asked);
        Assert.True(LongPath.FileExists(file));

        building.SetResult(Policy());

        var report = await removing;

        Assert.Single(prompt.Asked);
        Assert.Single(report!.Removed);
        Assert.False(LongPath.FileExists(file));
    }

    /// <summary>
    /// A build that fails refuses everything it was given, with the reason, and asks nothing. It is
    /// reported rather than thrown, so it reaches the user as a refusal of what they picked.
    /// </summary>
    [Fact]
    public async Task ARemovalWhosePolicyWillNotBuildRefusesEverythingAndAsksNothing()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromException<ExploreActionPolicy>(new IOException("a probe failed")), prompt);

        var report = await actions.RemoveAsync([Item(file)], ExploreRemovalMode.Permanent);

        var refused = Assert.Single(report!.Refused);
        Assert.Contains("could not work out what it has to protect", refused.Message);
        Assert.Empty(report.Removed);
        Assert.Empty(prompt.Asked);
        Assert.True(LongPath.FileExists(file));
    }

    /// <summary>
    /// Nothing is asked about a selection the policy refuses outright. A dialog covering something
    /// that is then refused teaches the user that saying yes is how to find out what happens.
    /// </summary>
    [Fact]
    public async Task AWhollyRefusedSelectionIsReportedWithoutAsking()
    {
        var kept = _temp.CreateFile(8, "kept", "a.bin");
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy(refusing: Path.GetDirectoryName(kept)!)), prompt);

        var report = await actions.RemoveAsync([Item(kept)], ExploreRemovalMode.RecycleBin);

        Assert.Equal(Reason, Assert.Single(report!.Refused).Message);
        Assert.Empty(prompt.Asked);
        Assert.True(LongPath.FileExists(kept));
    }

    /// <summary>
    /// Where some of a selection is allowed, the question is about that part alone, and the rest is
    /// still reported with its reason rather than silently dropped from the count.
    /// </summary>
    [Fact]
    public async Task TheUserIsAskedOnlyAboutWhatThePolicyAllows()
    {
        var kept = Item(_temp.CreateFile(8, "kept", "a.bin"));
        var loose = Item(_temp.CreateFile(16, "loose.bin"));
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy(refusing: Path.GetDirectoryName(kept.Path)!)), prompt);

        var report = await actions.RemoveAsync([kept, loose], ExploreRemovalMode.RecycleBin);

        Assert.Equal(ExploreRemovalPrompt.For(ExploreRemovalMode.RecycleBin, [loose]), Assert.Single(prompt.Asked));
        Assert.Equal(loose.Path, Assert.Single(report!.Removed).Path);
        Assert.Equal(kept.Path, Assert.Single(report.Refused).Path);
    }

    [Fact]
    public async Task DecliningRemovesNothingAndIsNotAFailure()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var actions = Actions(_ => Task.FromResult(Policy()), new FakeExploreConfirmation(answer: false));

        Assert.Null(await actions.RemoveAsync([Item(file)], ExploreRemovalMode.Permanent));
        Assert.True(LongPath.FileExists(file));
    }

    /// <summary>
    /// Run <paramref name="body"/> with no synchronisation context, so Ready is raised inline as the
    /// build completes rather than posted to the test runner's own context and heard later, on
    /// another thread, after the assertions have run.
    /// </summary>
    private static void WithoutContext(Action body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private ExploreActions Actions(
        Func<CancellationToken, Task<ExploreActionPolicy>> build, FakeExploreConfirmation? prompt = null) =>
        new(build, () => prompt ?? new FakeExploreConfirmation(answer: false), _faults, new FakeRecycleBin());

    private static ExploreActionPolicy Policy(string? refusing = null) =>
        new(
            refusing is null ? [] : [ProtectedRegion.Refusing(refusing, RegionScope.PathAndBelow, Reason)],
            [],
            new FakeVolumeInventory());

    private static ExploreItem Item(string file) => new(file, IsDirectory: false, Bytes: new FileInfo(file).Length);
}
