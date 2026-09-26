using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore selection holds Core's decisions together: what it names, what it offers, what
/// it is told when the policy lands, and what it remembers of a removal. The decisions themselves
/// are asserted in Core; these assert that the selection asks them, at the right moment, and says
/// what they answered.
/// </summary>
public sealed class ExploreSelectionTests : IDisposable
{
    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    [Fact]
    public void TheLabelNamesOneItemAndCountsSeveral()
    {
        var (tree, folder, file) = Scanned();
        var selection = Selection(tree);

        Assert.Equal(string.Empty, selection.Label);

        selection.Select([file]);

        Assert.Equal($"Selected: {tree.PathOf(file)}", selection.Label);
        Assert.Equal(FreeSpace.Format(5), selection.Figures);

        selection.Select([folder, file]);

        Assert.Equal("Selected: 2 items", selection.Label);
        Assert.Equal(FreeSpace.Format(15), selection.Figures);
    }

    /// <summary>
    /// Removing takes something picked; opening, revealing and the properties sheet take exactly one
    /// thing. None of them is offered while the page is busy, because a removal and a scan of the
    /// same drive have no business overlapping.
    /// </summary>
    [Fact]
    public void EachActionIsOfferedOnlyForWhatItCanActOn()
    {
        var (tree, folder, file) = Scanned();
        var selection = Selection(tree);

        Assert.False(selection.DeleteCommand.CanExecute(null));
        Assert.False(selection.OpenCommand.CanExecute(null));

        selection.Select([file]);

        Assert.True(selection.DeleteCommand.CanExecute(null));
        Assert.True(selection.DeletePermanentlyCommand.CanExecute(null));
        Assert.True(selection.OpenCommand.CanExecute(null));
        Assert.True(selection.RevealCommand.CanExecute(null));
        Assert.True(selection.PropertiesCommand.CanExecute(null));

        selection.Select([folder, file]);

        Assert.True(selection.DeleteCommand.CanExecute(null));
        Assert.False(selection.OpenCommand.CanExecute(null));
        Assert.False(selection.RevealCommand.CanExecute(null));

        selection.CanAct = false;

        Assert.False(selection.DeleteCommand.CanExecute(null));
        Assert.False(selection.DeletePermanentlyCommand.CanExecute(null));
    }

    /// <summary>§7.1: Explore never pre-selects, so pointing at a tree picks nothing.</summary>
    [Fact]
    public void ShowingATreePicksNothing()
    {
        var (tree, _, file) = Scanned();
        var selection = Selection(tree);

        selection.Select([file]);
        selection.Show(tree);

        Assert.Empty(selection.Nodes);
    }

    /// <summary>
    /// A tree that continues the one on screen keeps what still names what it named, and drops what
    /// does not rather than putting something else in its place: that would be the tool choosing
    /// the target.
    /// </summary>
    [Fact]
    public void CarryingKeepsOnlyWhatStillNamesTheSameThing()
    {
        var (tree, folder, file) = Scanned();
        var selection = Selection(tree);

        // The same node numbers under other names, as a rescan that numbered its nodes afresh gives.
        var builder = new ExploreTreeBuilder(tree.PathOf(tree.RootNode));
        builder.AddChildren(ExploreTreeBuilder.RootNode, [ExploreFixture.Folder("old"), ExploreFixture.File("other.bin", 5)]);
        var rescan = builder.Build(ExploreChildOrder.BySize);

        selection.Select([folder, file]);
        selection.Carry(rescan);

        Assert.Equal([folder], selection.Nodes);
    }

    [Fact]
    public void TheNoteSaysWhyTheSelectionWillNotBeRemoved()
    {
        var (tree, _, file) = Scanned();
        _explore.Build = _ => Task.FromResult(_explore.Policy("Kept for a reason.", tree.PathOf(file)));
        var selection = Selection(tree);

        selection.Select([file]);

        Assert.Equal("Kept for a reason.", selection.Note);
        Assert.True(selection.HasNote);
    }

    /// <summary>
    /// The refusal is the one thing on the page whose answer arrives late. A selection made while
    /// the policy was still being built is told what it turned out to be, rather than being left
    /// holding "in a moment" — and told on the thread that owns it, because the note is bound.
    /// </summary>
    [Fact]
    public void TheNoteIsRestatedWhenThePolicyLands() => UiThread.Run(async () =>
    {
        var (tree, _, file) = Scanned();
        var building = new TaskCompletionSource<ExploreActionPolicy>();
        _explore.Build = _ => building.Task;
        var selection = Selection(tree);
        var owner = Environment.CurrentManagedThreadId;
        var restated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        selection.Select([file]);

        Assert.Contains("still working out what it has to protect", selection.Note);

        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExploreSelection.HasNote))
            {
                restated.TrySetResult(Environment.CurrentManagedThreadId);
            }
        };

        // Finished off the page's thread, as a build running its probes in the background does.
        await Task.Run(() => building.SetResult(_explore.Policy()));

        Assert.Equal(owner, await restated.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(selection.Note);
    });

    /// <summary>
    /// What went is remembered against the tree it went from: gone from what may be picked, the
    /// files inside it with it, and stated by the stale note — until a new tree arrives, whose node
    /// numbers mean something else.
    /// </summary>
    [Fact]
    public void ARemovalIsRememberedAgainstTheTreeItCameFrom() => UiThread.Run(async () =>
    {
        var (tree, folder, file) = Scanned();
        var selection = Selection(tree);
        var working = new List<bool>();
        var reported = new List<string>();
        var changed = 0;

        selection.Working += (_, busy) => working.Add(busy);
        selection.Reported += (_, sentence) => reported.Add(sentence);
        selection.Changed += (_, _) => changed++;

        selection.Select([folder]);
        await selection.DeleteCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(tree.PathOf(folder)));
        Assert.Equal([true, false], working);
        Assert.Equal(1, changed);
        Assert.StartsWith("Moved 'old'", Assert.Single(reported));
        Assert.Empty(selection.Nodes);

        Assert.True(selection.WasRemoved(folder));
        Assert.True(selection.WasRemoved(tree.ChildrenOf(folder)[0]));
        Assert.False(selection.WasRemoved(file));
        Assert.StartsWith("1 item(s) have been removed since this scan.", selection.StaleNote);

        selection.Select([folder, file]);

        Assert.Equal([file], selection.Nodes);

        var (rescan, _, _) = Scanned();
        selection.Show(rescan);

        Assert.False(selection.WasRemoved(folder));
        Assert.Null(selection.StaleNote);
    });

    /// <summary>
    /// Navigating while a removal runs empties the selection, because nothing blocks the list, the
    /// map or the trail until it ends. What went is still what was picked when it started, or the
    /// removed folder would stay listed and could be picked again.
    /// </summary>
    [Fact]
    public void ARemovalRecordsWhatWasPickedEvenIfTheSelectionWasCleared() => UiThread.Run(async () =>
    {
        var (tree, folder, file) = Scanned();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explore.Prompt = new FakeExploreConfirmation(answer.Task);
        var selection = Selection(tree);

        selection.Select([folder]);
        var removing = selection.DeleteCommand.ExecuteAsync(null);

        selection.Show(tree);
        answer.SetResult(true);
        await removing;

        Assert.False(Directory.Exists(tree.PathOf(folder)));
        Assert.True(selection.WasRemoved(folder));
        Assert.StartsWith("1 item(s) have been removed since this scan.", selection.StaleNote);

        // §5.6: what was not picked is still there, and so is the folder the scan started from.
        Assert.True(File.Exists(tree.PathOf(file)));
        Assert.True(Directory.Exists(tree.PathOf(tree.RootNode)));
        Assert.False(selection.WasRemoved(file));
    });

    /// <summary>
    /// Picking something else while a removal runs neither puts it in the record nor loses it. The
    /// record is of what was acted on, and the new pick is the user's, so it stays picked.
    /// </summary>
    [Fact]
    public void ARemovalLeavesAPickMadeWhileItRan() => UiThread.Run(async () =>
    {
        var (tree, folder, file) = Scanned();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explore.Prompt = new FakeExploreConfirmation(answer.Task);
        var selection = Selection(tree);

        selection.Select([folder]);
        var removing = selection.DeleteCommand.ExecuteAsync(null);

        selection.Select([file]);
        answer.SetResult(true);
        await removing;

        Assert.True(selection.WasRemoved(folder));
        Assert.False(selection.WasRemoved(file));
        Assert.True(File.Exists(tree.PathOf(file)));
        Assert.Equal([file], selection.Nodes);
    });

    /// <summary>
    /// A dismissed dialog says so. The sentence left standing from before would be read as the
    /// outcome of the dialog just closed.
    /// </summary>
    [Fact]
    public void DecliningSaysNothingWasRemoved() => UiThread.Run(async () =>
    {
        var (tree, folder, _) = Scanned();
        _explore.Prompt = new FakeExploreConfirmation(answer: false);
        var selection = Selection(tree);
        var reported = new List<string>();

        selection.Reported += (_, sentence) => reported.Add(sentence);
        selection.Select([folder]);
        await selection.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Nothing was removed."], reported);
        Assert.True(Directory.Exists(tree.PathOf(folder)));
        Assert.Null(selection.StaleNote);
    });

    private ExploreSelection Selection(ExploreTree tree)
    {
        var selection = new ExploreSelection(
            new ExploreActions(_explore.Build, () => _explore.Prompt, _explore.Faults, new FakeRecycleBin()));

        selection.Show(tree);

        return selection;
    }

    /// <summary>A folder holding one file, beside a file, all on the disk so a removal has something to remove.</summary>
    private (ExploreTree Tree, int Folder, int File) Scanned()
    {
        var root = _explore.Temp.CreateDirectory("scan");
        _explore.Temp.CreateFile(10, "scan", "old", "a.bin");
        _explore.Temp.CreateFile(5, "scan", "keep.bin");

        var builder = new ExploreTreeBuilder(root);
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            ExploreFixture.Folder("old"),
            ExploreFixture.File("keep.bin", 5),
        ]);

        builder.AddChildren(folder, [ExploreFixture.File("a.bin", 10)]);

        return (builder.Build(ExploreChildOrder.BySize), folder, folder + 1);
    }
}
