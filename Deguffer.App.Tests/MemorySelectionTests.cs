using Deguffer.App.ViewModels;
using Deguffer.Core.Diagnostics;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Memory page holds a pick and its one action together (§7.2.1): the answer read off the
/// window's thread, the press that beats it, the pick that changes under it, and the close watched to
/// its end. What a pick is and what a close comes to are Core's, and proven there; these prove the
/// order the page puts them in.
///
/// <para>The close is the real <see cref="ProcessCloser"/> over an invented machine, so every report
/// the page shows is one the closer produced, in the order it produced them.</para>
/// </summary>
public sealed class MemorySelectionTests : IDisposable
{
    /// <summary>
    /// Long enough for an answer that has been released to have landed on the page's thread, so a test
    /// that asserts it was ignored is not merely asserting it had not arrived yet.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();
    private readonly MemoryCloseMachine _machine = new();
    private readonly HeldFactSource _facts;
    private readonly CrashLog _faults;
    private readonly MemoryTree _tree;

    public MemorySelectionTests()
    {
        _facts = new HeldFactSource(_machine.Facts);
        _faults = new CrashLog(new FakeUserEnvironment(_temp.Path));
        _tree = MemoryTreeBuilder.Build(_machine.Before);
    }

    /// <summary>A question a failed test left held is let go, so no thread waits on it for the rest of the run.</summary>
    public void Dispose()
    {
        _facts.Release();
        _temp.Dispose();
    }

    private int TargetNode => _tree.Find(MemoryCloseMachine.TargetKey)!.Value;

    private int PartNode => _tree.Find(MemoryNodeKey.Of(MemoryPart.Windows))!.Value;

    private MemorySelection Selection(ScriptedMemoryPrompt? prompt = null)
    {
        var actions = new MemoryActions(
            new MemoryActionPolicy(_facts, _machine.Desktop, MemoryCloseMachine.Own),
            _machine.Closer(),
            () => prompt ?? new ScriptedMemoryPrompt(answer: false));

        var selection = new MemorySelection(actions, _faults);
        selection.Follow(_tree, MemoryViewChange.Navigation);

        return selection;
    }

    private string Logged() => File.Exists(_faults.FilePath) ? File.ReadAllText(_faults.FilePath) : string.Empty;

    /// <summary>A reading in which the program is numbered differently, which the test asserts before relying on it.</summary>
    private MemoryTree Renumbered()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(50, 1, "larger.exe", 9_000, 3)
            .Process(MemoryCloseMachine.Shell, 1, "explorer.exe", 200, 1)
            .Process(MemoryCloseMachine.Own, 1, "Deguffer.exe", 100, 2)
            .Process(MemoryCloseMachine.TargetId, MemoryCloseMachine.Shell, "editor.exe", 300, MemoryCloseMachine.TargetCreated)
            .Build());

        Assert.NotEqual(TargetNode, tree.Find(MemoryCloseMachine.TargetKey));

        return tree;
    }

    [Fact]
    public void PickingAProgramSaysWindowsIsBeingAskedThenWhatItAnswered() => UiThread.Run(async () =>
    {
        _facts.Hold();
        var selection = Selection();

        selection.Select(TargetNode);

        Assert.Equal("Deguffer is asking Windows about this program.", selection.Note);
        Assert.Contains("editor.exe", selection.Label, StringComparison.Ordinal);

        _facts.Release();
        await Eventually.HoldsAsync(() => selection.Note != "Deguffer is asking Windows about this program.", "the answer landed");

        Assert.Equal(MemoryVerdict.Allow([]).Reason, selection.Note);
        Assert.Equal(1, _facts.Asked);
    });

    /// <summary>§7.2.1: nothing is asked of Windows for a node that is not a program.</summary>
    [Fact]
    public void PickingAPartOfThePictureSaysSoAndAsksWindowsNothing()
    {
        var selection = Selection();

        selection.Select(PartNode);

        Assert.True(selection.HasSelection);
        Assert.Equal(MemoryTarget.NotAProgram.Reason, selection.Note);
        Assert.Equal(0, _facts.Asked);
    }

    [Fact]
    public void ANumberOutsideTheTreeOnScreenPicksNothing()
    {
        var selection = Selection();

        selection.Select(_tree.NodeCount);

        Assert.False(selection.HasSelection);
        Assert.Equal(string.Empty, selection.Note);
        Assert.False(selection.CloseCommand.CanExecute(null));
    }

    /// <summary>
    /// A reader who picks a second thing before the first answer lands must not be told about the
    /// first. The answer here is already on its way when the pick changes, so only the page can drop it.
    /// </summary>
    [Fact]
    public void AnAnswerForAnEarlierPickIsNotShownAgainstTheNextOne() => UiThread.Run(async () =>
    {
        _facts.Hold();
        var selection = Selection();

        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => _facts.Asked == 1, "the question was put");
        selection.Select(PartNode);

        _facts.Release();
        await Eventually.HoldsAsync(() => _facts.Answered == 1, "the first question was answered");
        await Task.Delay(Settle);

        Assert.Equal(MemoryTarget.NotAProgram.Reason, selection.Note);
        Assert.Equal(PartNode, selection.Node);
    });

    /// <summary>
    /// A press that beats Windows' answer waits for it, because this is the path that sends the
    /// message: a close decided against an answer that has not arrived is the thing deferring it must
    /// never buy.
    /// </summary>
    [Fact]
    public void APressBeforeTheAnswerWaitsForItAndThenAsksTheUser() => UiThread.Run(async () =>
    {
        _facts.Hold();
        var prompt = new ScriptedMemoryPrompt(answer: false);
        var selection = Selection(prompt);

        selection.Select(TargetNode);
        var pressing = selection.CloseCommand.ExecuteAsync(null);

        Assert.Equal(0, prompt.Asked);

        _facts.Release();
        await pressing;

        Assert.Equal(1, prompt.Asked);
        Assert.Equal("editor.exe (process 4321) was not asked to close. Nothing was sent.", selection.Report.Statement);
        Assert.Empty(_machine.Windows.Posted);
    });

    /// <summary>§7.2 acts on what the user picked and on nothing else, and a press is about the pick it was made on.</summary>
    [Fact]
    public void APressWhosePickChangesWhileItWaitsDoesNothing() => UiThread.Run(async () =>
    {
        _facts.Hold();
        var prompt = new ScriptedMemoryPrompt(answer: true);
        var selection = Selection(prompt);

        selection.Select(TargetNode);
        var pressing = selection.CloseCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => _facts.Asked == 1, "the question was put");

        selection.Select(PartNode);
        _facts.Release();
        await pressing;

        Assert.Equal(0, prompt.Asked);
        Assert.False(selection.Report.HasReport);
        Assert.Empty(_machine.Windows.Posted);
    });

    /// <summary>A refusal is a sentence, never a disabled button, and a refused press asks the user nothing.</summary>
    [Fact]
    public void ARefusedPickIsOfferedAndAPressSaysWhyWithoutAskingTheUser() => UiThread.Run(async () =>
    {
        var prompt = new ScriptedMemoryPrompt(answer: true);
        var selection = Selection(prompt);
        var own = _tree.Find(new MemoryNodeKey(MemoryPart.Process, MemoryCloseMachine.Own, 2))!.Value;

        selection.Select(own);
        await Eventually.HoldsAsync(() => _facts.Answered == 1, "the policy was asked");
        await Eventually.HoldsAsync(() => selection.Note != "Deguffer is asking Windows about this program.", "the answer landed");

        Assert.True(selection.CloseCommand.CanExecute(null));

        await selection.CloseCommand.ExecuteAsync(null);

        Assert.Equal(0, prompt.Asked);
        Assert.Equal(selection.Note, selection.Report.Statement);
        Assert.False(selection.Report.IsWatching);
    });

    /// <summary>
    /// Windows would not answer, so the page says so, the log says which call failed, and a press is
    /// answered with the same refusal rather than finding no verdict at all.
    /// </summary>
    [Fact]
    public void AQuestionWindowsWouldNotAnswerIsRecordedAndRefuses() => UiThread.Run(async () =>
    {
        _facts.Refuses = true;
        var prompt = new ScriptedMemoryPrompt(answer: true);
        var selection = Selection(prompt);

        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => selection.Note == MemoryActionPolicy.Unanswered.Reason, "the refusal was said");

        Assert.Contains("Deciding whether Memory may close a program", Logged(), StringComparison.Ordinal);

        await selection.CloseCommand.ExecuteAsync(null);

        Assert.Equal(0, prompt.Asked);
        Assert.Equal(MemoryActionPolicy.Unanswered.Reason, selection.Report.Statement);
    });

    /// <summary>Recorded whatever became of the pick, and said only about the pick it belongs to.</summary>
    [Fact]
    public void AnUnansweredQuestionForAnEarlierPickIsRecordedButNotSaid() => UiThread.Run(async () =>
    {
        _facts.Hold();
        _facts.Refuses = true;
        var selection = Selection();

        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => _facts.Asked == 1, "the question was put");
        selection.Select(PartNode);

        _facts.Release();
        await Eventually.HoldsAsync(() => Logged().Length > 0, "the failure was recorded");
        await Task.Delay(Settle);

        Assert.Equal(MemoryTarget.NotAProgram.Reason, selection.Note);
    });

    /// <summary>
    /// A reading renumbers every node, and the pick follows the program rather than the number. The
    /// verdict goes with it: reading it again twice a second would open that process thirty times a
    /// minute for an answer nobody asked for.
    /// </summary>
    [Fact]
    public void AReadingCarriesThePickAndItsVerdictWithoutAskingAgain() => UiThread.Run(async () =>
    {
        var selection = Selection();
        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => selection.Note == MemoryVerdict.Allow([]).Reason, "the answer landed");

        var renumbered = Renumbered();
        selection.Follow(renumbered, MemoryViewChange.Reading);

        Assert.Equal(renumbered.Find(MemoryCloseMachine.TargetKey), selection.Node);
        Assert.Equal(MemoryVerdict.Allow([]).Reason, selection.Note);
        Assert.Equal(1, _facts.Asked);
    });

    [Fact]
    public void AReadingWithoutTheProgramDropsThePick()
    {
        var selection = Selection();
        selection.Select(TargetNode);

        selection.Follow(MemoryTreeBuilder.Build(_machine.After), MemoryViewChange.Reading);

        Assert.False(selection.HasSelection);
        Assert.Equal(string.Empty, selection.Note);
    }

    [Fact]
    public void ANavigationDropsThePick()
    {
        var selection = Selection();
        selection.Select(PartNode);

        selection.Follow(_tree, MemoryViewChange.Navigation);

        Assert.False(selection.HasSelection);
        Assert.Equal(string.Empty, selection.Label);
    }

    /// <summary>
    /// A reading does not pick anything, and says nothing about a selection that has not changed:
    /// otherwise every reading announces one twice a second for the life of the page.
    /// </summary>
    [Fact]
    public void AReadingWithNothingPickedRaisesNothing()
    {
        var selection = Selection();
        var raised = new List<string?>();
        selection.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        selection.Follow(Renumbered(), MemoryViewChange.Reading);

        Assert.Empty(raised);
        Assert.False(selection.HasSelection);
    }

    /// <summary>
    /// §7.2.1: one close at a time. While the program is being watched no second close is offered,
    /// and the report stands until it has exited.
    /// </summary>
    [Fact]
    public void AWatchedCloseOffersNoSecondCloseUntilTheProgramHasGone() => UiThread.Run(async () =>
    {
        var selection = Selection(new ScriptedMemoryPrompt(answer: true));
        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => selection.Note == MemoryVerdict.Allow([]).Reason, "the answer landed");

        var closing = selection.CloseCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => selection.Report.IsWatching, "the watch was reported");
        await _machine.Clock.WhenWaitingAsync(Patience);

        Assert.False(selection.CloseCommand.CanExecute(null));
        Assert.Single(_machine.Windows.Posted);

        _machine.Target.Exited = true;
        _machine.Clock.Advance(ProcessCloser.WatchCadence);
        await closing;

        Assert.False(selection.Report.IsWatching);
        Assert.True(selection.Report.HasFigures);
        Assert.True(selection.CloseCommand.CanExecute(null));
    });

    /// <summary>
    /// Taking the report down is one of the watch's three ends. The close goes on to its own end, and
    /// putting what it came to back on screen would undo the dismissal.
    /// </summary>
    [Fact]
    public void ADismissedReportStaysDownWhatTheCloseComesTo() => UiThread.Run(async () =>
    {
        var selection = Selection(new ScriptedMemoryPrompt(answer: true));
        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => selection.Note == MemoryVerdict.Allow([]).Reason, "the answer landed");

        var closing = selection.CloseCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => selection.Report.IsWatching, "the watch was reported");

        selection.Report.DismissCommand.Execute(null);
        await closing.WaitAsync(Patience);

        Assert.False(selection.Report.HasReport);
        Assert.True(selection.CloseCommand.CanExecute(null));
    });

    /// <summary>
    /// The machine moved between the pick and the confirmation, and the second decision, made with the
    /// handle held, is what the report says.
    /// </summary>
    [Fact]
    public void AProgramThatWentBeforeTheConfirmationIsReportedAsGoneAndSentNothing() => UiThread.Run(async () =>
    {
        var selection = Selection(new ScriptedMemoryPrompt(answer: true));
        selection.Select(TargetNode);
        await Eventually.HoldsAsync(() => selection.Note == MemoryVerdict.Allow([]).Reason, "the answer landed");

        _machine.Target.Exited = true;
        await selection.CloseCommand.ExecuteAsync(null);

        Assert.Contains("has gone", selection.Report.Statement, StringComparison.Ordinal);
        Assert.False(selection.Report.HasFigures);
        Assert.Empty(_machine.Windows.Posted);
    });

    /// <summary>What the page reads to put the highlight back: every change of pick names the node.</summary>
    [Fact]
    public void EveryPickSaysWhichNodeIsSelected()
    {
        var selection = Selection();
        var raised = new List<string?>();
        selection.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        selection.Select(PartNode);
        selection.Select(null);

        Assert.Equal(2, raised.Count(name => name == nameof(MemorySelection.Node)));
    }
}
