using System.Collections.Concurrent;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
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
    private readonly RunningActions _running = new();
    private FakeRecycleBin _recycleBin = new();

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
    /// A failed build says so rather than that an answer is on its way, and still refuses: without the probed half
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
        actions.Reconsider(ProtectionScope.Everything);

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

    /// <summary>
    /// A screen marking what is about to go is told what the removal will act on before the user is
    /// asked, and it is the part the policy allows alone: the refused part is never marked as going,
    /// because it is not (§7.1).
    /// </summary>
    [Fact]
    public async Task WhatWillBeActedOnIsToldBeforeAskingAndIsOnlyWhatThePolicyAllows()
    {
        var kept = Item(_temp.CreateFile(8, "kept", "a.bin"));
        var loose = Item(_temp.CreateFile(16, "loose.bin"));
        var prompt = new FakeExploreConfirmation(answer: false);
        var actions = Actions(_ => Task.FromResult(Policy(refusing: Path.GetDirectoryName(kept.Path)!)), prompt);
        IReadOnlyList<ExploreItem>? told = null;
        var askedFirst = -1;

        await actions.RemoveAsync([kept, loose], ExploreRemovalMode.RecycleBin, allowed =>
        {
            told = allowed;
            askedFirst = prompt.Asked.Count;
        });

        Assert.Equal([loose], told);
        Assert.Equal(0, askedFirst);
        Assert.Single(prompt.Asked);
    }

    /// <summary>
    /// An item the Recycle Bin cannot take is not put to the user as going there: the prompt would
    /// promise a move that never happens. It goes straight to the report with why, and nothing is
    /// handed to the bin to move.
    /// </summary>
    [Fact]
    public async Task AnItemTheBinCannotTakeIsReportedWithoutAsking()
    {
        var file = _temp.CreateFile(8, "a.bin");
        _recycleBin.CannotTake = _ => "Too long for the bin.";
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy()), prompt);

        var report = await actions.RemoveAsync([Item(file)], ExploreRemovalMode.RecycleBin);

        Assert.Empty(prompt.Asked);
        Assert.Empty(_recycleBin.Paths);
        Assert.StartsWith("Too long for the bin. ", Assert.Single(report!.Refused).Message, StringComparison.Ordinal);
        Assert.Equal(report.Refused[0].Message, report.Summary);
        Assert.True(LongPath.FileExists(file));
    }

    /// <summary>
    /// Where the bin can take part of a selection, the question, and what is marked as going, are
    /// about that part alone. The rest is reported with why and is still there, and so is everything
    /// beside it (§5.6). The bin is asked in the form the shell is handed, never the extended-length
    /// one (§6.3).
    /// </summary>
    [Fact]
    public async Task TheUserIsAskedOnlyAboutWhatTheBinCanTake()
    {
        var stays = Item(_temp.CreateFile(8, "stays.bin"));
        var goes = Item(_temp.CreateFile(16, "goes.bin"));
        var beside = _temp.CreateFile(4, "beside.bin");
        _recycleBin.CannotTake = path => path == stays.Path ? "Too long for the bin." : null;
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy()), prompt);
        IReadOnlyList<ExploreItem>? told = null;

        var report = await actions.RemoveAsync([stays, goes], ExploreRemovalMode.RecycleBin, allowed => told = allowed);

        Assert.Equal([goes], told);
        Assert.Equal(ExploreRemovalPrompt.For(ExploreRemovalMode.RecycleBin, [goes]), Assert.Single(prompt.Asked));
        // One question of the whole selection, so each drive's bin is read once for the prompt.
        Assert.Equal([stays.Path, goes.Path], Assert.Single(_recycleBin.Questions));
        Assert.All(_recycleBin.Asked, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));
        Assert.Equal(goes.Path, Assert.Single(report!.Removed).Path);
        Assert.Equal(stays.Path, Assert.Single(report.Refused).Path);
        Assert.True(report.Verification.Passed);
        Assert.True(LongPath.FileExists(stays.Path));
        Assert.True(LongPath.FileExists(beside));
    }

    /// <summary>
    /// An item kept out of the question because the bin could not take it stays out of the removal,
    /// even where the bin would take it by then: the user was never asked about it.
    /// </summary>
    [Fact]
    public async Task AnItemTheBinRefusedBeforeTheQuestionIsNotRemovedOnceItCouldBeTaken()
    {
        var stays = Item(_temp.CreateFile(8, "stays.bin"));
        var goes = Item(_temp.CreateFile(16, "goes.bin"));
        var askedAboutStays = 0;
        _recycleBin.CannotTake = path => path == stays.Path && askedAboutStays++ == 0 ? "Too long for the bin." : null;
        var actions = Actions(_ => Task.FromResult(Policy()), new FakeExploreConfirmation(answer: true));

        var report = await actions.RemoveAsync([stays, goes], ExploreRemovalMode.RecycleBin);

        Assert.Equal([goes.Path], _recycleBin.Paths);
        Assert.Equal(stays.Path, Assert.Single(report!.Refused).Path);
        Assert.True(LongPath.FileExists(stays.Path));
    }

    /// <summary>
    /// The bin is asked off the calling thread. A folder is answered by a walk of everything inside
    /// it, and the caller is a page's thread, which would freeze for as long as the walk takes.
    /// </summary>
    [Fact]
    public async Task TheBinIsAskedOffTheCallingThread()
    {
        var file = Item(_temp.CreateFile(8, "a.bin"));
        var caller = Environment.CurrentManagedThreadId;
        int? askedOn = null;
        _recycleBin.CannotTake = _ =>
        {
            askedOn = Environment.CurrentManagedThreadId;
            return null;
        };
        var actions = Actions(_ => Task.FromResult(Policy()), new FakeExploreConfirmation(answer: false));

        await actions.RemoveAsync([file], ExploreRemovalMode.RecycleBin);

        Assert.NotNull(askedOn);
        Assert.NotEqual(caller, askedOn);
    }

    /// <summary>
    /// A removal cancelled while the bin is being asked stops there: nothing more is asked of the
    /// bin, the user is asked nothing, and nothing is removed.
    /// </summary>
    [Fact]
    public async Task CancellingWhileTheBinIsAskedStopsBeforeTheQuestion()
    {
        var first = Item(_temp.CreateFile(8, "first.bin"));
        var second = Item(_temp.CreateFile(8, "second.bin"));
        using var cancel = new CancellationTokenSource();
        _recycleBin.CannotTake = _ =>
        {
            cancel.Cancel();
            return null;
        };
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy()), prompt);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => actions.RemoveAsync([first, second], ExploreRemovalMode.RecycleBin, ct: cancel.Token));

        Assert.Equal([first.Path], _recycleBin.Asked);
        Assert.Empty(prompt.Asked);
        Assert.Empty(_recycleBin.Paths);
        Assert.True(LongPath.FileExists(first.Path));
        Assert.True(LongPath.FileExists(second.Path));
    }

    /// <summary>
    /// §5.6 asserts an item the bin could not take survived, as it does for every item the policy
    /// allows. Here a removal in another folder takes it as well, and nothing else beside the removed
    /// item would show that.
    /// </summary>
    [Fact]
    public async Task AnItemTheBinCouldNotTakeIsAssertedToHaveSurvived()
    {
        var stays = Item(_temp.CreateFile(8, "kept", "stays.bin"));
        var goes = Item(_temp.CreateFile(16, "going", "goes.bin"));
        _recycleBin = new FakeRecycleBin(path =>
        {
            File.Delete(path);
            File.Delete(stays.Path);
            return new RecycleOutcome(Removed: true);
        })
        {
            CannotTake = path => path == stays.Path ? "Too long for the bin." : null,
        };
        var actions = Actions(_ => Task.FromResult(Policy()), new FakeExploreConfirmation(answer: true));

        var report = await actions.RemoveAsync([stays, goes], ExploreRemovalMode.RecycleBin);

        Assert.Equal(stays.Path, Assert.Single(report!.Refused).Path);
        Assert.False(report.Verification.Passed);
        Assert.Contains(report.Verification.Failures, check => check.Subject == Path.GetDirectoryName(stays.Path));
    }

    /// <summary>A permanent removal goes nowhere near the bin, so the bin is not asked about it.</summary>
    [Fact]
    public async Task APermanentRemovalDoesNotAskTheBin()
    {
        var file = Item(_temp.CreateFile(8, "a.bin"));
        _recycleBin.CannotTake = _ => "Too long for the bin.";
        var prompt = new FakeExploreConfirmation(answer: false);
        var actions = Actions(_ => Task.FromResult(Policy()), prompt);

        await actions.RemoveAsync([file], ExploreRemovalMode.Permanent);

        Assert.Empty(_recycleBin.Asked);
        Assert.Equal(ExploreRemovalPrompt.For(ExploreRemovalMode.Permanent, [file]), Assert.Single(prompt.Asked));
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
    /// A removal is recorded as running from its confirmation until its §5.6 verification has
    /// reported, so neither the window nor an Elevate button ends the process under it. Not while
    /// the question is open: nothing has changed then, and a window closed over it loses nothing.
    /// </summary>
    [Fact]
    public async Task ARemovalIsRecordedAsRunningFromItsConfirmationToItsReport()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<RunningAction> whileRemoving = [];
        _recycleBin = new FakeRecycleBin(path =>
        {
            whileRemoving = _running.Current;
            File.Delete(path);
            return new RecycleOutcome(Removed: true);
        });
        var actions = Actions(_ => Task.FromResult(Policy()), new FakeExploreConfirmation(answer.Task));

        var removing = actions.RemoveAsync([Item(file)], ExploreRemovalMode.RecycleBin);

        Assert.True(_running.MayEndProcess);

        answer.SetResult(true);
        var report = await removing;

        Assert.Equal([RunningAction.ExploreRemoval], whileRemoving);
        Assert.True(_running.MayEndProcess);
        Assert.Single(report!.Removed);
    }

    /// <summary>
    /// A path is asked about once for each build, because each answer asks the machine where the
    /// path's volume is mounted and the page asks about the same paths many times over. A new build
    /// knows nothing, because it is built to look at the machine afresh.
    /// </summary>
    [Fact]
    public void APathIsAskedAboutOnceForEachBuild()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var volumes = new FakeVolumeInventory();
        var actions = Actions(_ => Task.FromResult(Policy(volumes: volumes)));

        actions.Prepare();

        Assert.True(actions.Verdict(file).IsAllowed);

        var asked = volumes.MountPointQueries.Count;

        Assert.True(asked > 0, "the policy never asked the machine, so nothing here proves it was not asked again");

        Assert.True(actions.Verdict(file).IsAllowed);
        Assert.Equal(asked, volumes.MountPointQueries.Count);

        actions.Reconsider(ProtectionScope.Everything);
        actions.Verdict(file);

        Assert.True(volumes.MountPointQueries.Count > asked);
    }

    /// <summary>
    /// What the page was told is kept, and what a removal acts on is not: it asks the policy again.
    /// A volume mounted at a folder after the page was told it may go makes that folder the top of a
    /// volume, and removing it would remove the whole volume.
    /// </summary>
    [Fact]
    public async Task ARemovalAsksThePolicyAgainRatherThanReadingWhatThePageWasTold()
    {
        var folder = _temp.CreateDirectory("mounted");
        var volumes = new FakeVolumeInventory();
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy(volumes: volumes)), prompt);

        actions.Prepare();

        Assert.True(actions.Verdict(folder).IsAllowed);

        volumes.With(folder + Path.DirectorySeparatorChar);

        Assert.True(actions.Verdict(folder).IsAllowed);

        var report = await actions.RemoveAsync([new ExploreItem(folder, IsDirectory: true, Bytes: 0, LowerBound: false)], ExploreRemovalMode.Permanent);

        Assert.Contains("whole drive", Assert.Single(report!.Refused).Message);
        Assert.Empty(prompt.Asked);
        Assert.True(Directory.Exists(folder));
    }

    /// <summary>
    /// The note is given on the spot only where nothing has to be asked: while the policy is being
    /// built, or for paths the build has already answered. Anything else is for
    /// <see cref="ExploreActions.NoteAsync"/>, which answers what the build is asked once.
    /// </summary>
    [Fact]
    public async Task TheNoteIsGivenOnTheSpotOnlyFromWhatIsAlreadyKnown()
    {
        var kept = _temp.CreateFile(8, "kept", "a.bin");
        var loose = _temp.CreateFile(8, "loose.bin");
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        var volumes = new FakeVolumeInventory();
        var actions = Actions(_ => building.Task);
        ExploreItem[] items = [Item(kept), Item(loose)];

        actions.Prepare();

        Assert.True(actions.TryNote([Item(loose)], out var working));
        Assert.Contains("still working out what it has to protect", working);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.Ready += (_, _) => ready.TrySetResult();

        building.SetResult(Policy(refusing: Path.GetDirectoryName(kept)!, volumes: volumes));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The policy follows its own rules to where they are mounted as it is built.
        var built = volumes.MountPointQueries.Count;

        Assert.False(actions.TryNote(items, out _));
        Assert.Equal(built, volumes.MountPointQueries.Count);

        var asked = await actions.NoteAsync(items, CancellationToken.None);
        var questions = volumes.MountPointQueries.Count;

        Assert.Equal($"One of these 2 items will not be removed: {Reason}", asked);
        Assert.True(actions.TryNote(items, out var known));
        Assert.Equal(asked, known);
        Assert.Equal(questions, volumes.MountPointQueries.Count);
    }

    /// <summary>
    /// The note is worked out off the calling thread, which is the page's: a list selects any number
    /// of rows at once, and each is a question to the machine.
    /// </summary>
    [Fact]
    public async Task TheNoteIsWorkedOutOffTheCallingThread()
    {
        var file = _temp.CreateFile(8, "a.bin");
        var volumes = new FakeVolumeInventory();
        var threads = new ConcurrentBag<int>();
        var actions = Actions(_ => Task.FromResult(Policy(volumes: volumes)));

        volumes.Asked = _ => threads.Add(Environment.CurrentManagedThreadId);
        actions.Prepare();

        var caller = Environment.CurrentManagedThreadId;

        await actions.NoteAsync([Item(file)], CancellationToken.None);

        Assert.NotEmpty(threads);
        Assert.DoesNotContain(caller, threads);
    }

    /// <summary>
    /// A removal asks the policy about every item once before the question and once after it, and
    /// never on the page's thread: each question goes to the volume manager, and a selection of
    /// twenty thousand items asked there held the window for seconds before the dialog appeared and
    /// again after it was answered. The answers still come back in the order the items were picked.
    /// </summary>
    [Fact]
    public async Task ARemovalAsksThePolicyAboutEachItemOffThePagesThreadBeforeAndAfterTheQuestion()
    {
        var refusedFolder = _temp.CreateDirectory("kept");
        ExploreItem[] items =
        [
            .. Enumerable.Range(0, 12).Select(i => Item(i % 3 == 0
                ? _temp.CreateFile(8, "kept", $"{i}.bin")
                : _temp.CreateFile(8, "loose", $"{i}.bin"))),
        ];
        var volumes = new FakeVolumeInventory();
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy(refusing: refusedFolder, volumes: volumes)), prompt);
        var page = new QueuedContext();
        var pageThread = Environment.CurrentManagedThreadId;
        var asked = new ConcurrentQueue<(string Path, int Thread, bool AfterTheQuestion)>();

        IReadOnlyList<ExploreItem> acting = [];

        actions.Prepare();
        volumes.Asked = path => asked.Enqueue((LongPath.Display(path), Environment.CurrentManagedThreadId, prompt.FirstAsked.IsCompleted));

        Task<ExploreRemovalReport?> removing;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(page);

        try
        {
            removing = actions.RemoveAsync(items, ExploreRemovalMode.Permanent, acting: going => acting = going);
            page.RunUntil(() => removing.IsCompleted, "the removal");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var report = await removing;

        Assert.DoesNotContain(asked, question => question.Thread == pageThread);
        Assert.All(items, item =>
        {
            var before = asked.Count(question => question.Path == item.Path && !question.AfterTheQuestion);
            var after = asked.Count(question => question.Path == item.Path && question.AfterTheQuestion);

            Assert.True(before > 0, $"'{item.Path}' was never asked about before the question");
            Assert.Equal(before, after);
        });

        string[] loose = [.. items.Where(item => !item.Path.StartsWith(refusedFolder, StringComparison.Ordinal)).Select(item => item.Path)];
        string[] kept = [.. items.Where(item => item.Path.StartsWith(refusedFolder, StringComparison.Ordinal)).Select(item => item.Path)];

        Assert.Single(prompt.Asked);
        Assert.Equal(loose, acting.Select(item => item.Path));
        Assert.Equal(loose, report!.Removed.Select(outcome => outcome.Path));
        Assert.Equal(kept, report.Refused.Select(outcome => outcome.Path));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"'{path}' was refused and must survive"));
        Assert.True(report.Verification.Passed);
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

    /// <summary>
    /// The last word is asked after the user agrees and before anything moves. An item it withdraws is
    /// left where it is with its reason, is never handed to the bin, and is one of the things §5.6
    /// asserts survived the removal of the item beside it.
    /// </summary>
    [Fact]
    public async Task AnItemTheLastWordWithdrawsAfterTheQuestionIsLeftAndAssertedToSurvive()
    {
        var going = Item(_temp.CreateFile(8, "Links", "going.lnk"));
        var withdrawn = Item(_temp.CreateFile(8, "Links", "back.lnk"));
        var prompt = new FakeExploreConfirmation(answer: true);
        var actions = Actions(_ => Task.FromResult(Policy()), prompt);
        var askedAfter = -1;

        var report = await actions.RemoveAsync(
            [going, withdrawn],
            ExploreRemovalMode.RecycleBin,
            lastWord: item =>
            {
                askedAfter = prompt.Asked.Count;
                return Path.GetFileName(item.Path) == "back.lnk" ? "Its target is back." : null;
            });

        Assert.NotNull(report);
        Assert.Equal(1, askedAfter);
        Assert.Equal([going.Path], _recycleBin.Paths);
        Assert.Equal(going.Path, Assert.Single(report.Removed).Path);
        var refused = Assert.Single(report.Refused);
        Assert.Equal((withdrawn.Path, "Its target is back."), (refused.Path, refused.Message));
        Assert.True(File.Exists(withdrawn.Path));
        Assert.True(report.Verification.Passed);
        Assert.Contains(report.Verification.Checks, check => check.Detail == "All 1 other item(s) are still there.");
    }

    private ExploreActions Actions(
        Func<CancellationToken, Task<ExploreActionPolicy>> build, FakeExploreConfirmation? prompt = null) =>
        new((_, ct) => build(ct), () => prompt ?? new FakeExploreConfirmation(answer: false), _faults, _running, _recycleBin);

    private static ExploreActionPolicy Policy(string? refusing = null, FakeVolumeInventory? volumes = null) =>
        new(
            refusing is null ? [] : [ProtectedRegion.Refusing(refusing, RegionScope.PathAndBelow, Reason)],
            [],
            volumes ?? new FakeVolumeInventory());

    private static ExploreItem Item(string file) => new(file, IsDirectory: false, Bytes: new FileInfo(file).Length, LowerBound: false);
}
