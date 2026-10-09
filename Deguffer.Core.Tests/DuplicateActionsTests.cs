using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// When a rule, a confirmation and a removal may read a search's groups, and how a removal is asked
/// about and carried out (§7.4). The search adds its groups on the page's thread, and each of these
/// reads them on another, so none may start until the search has ended.
/// </summary>
public sealed class DuplicateActionsTests : DuplicateRemovalScene
{
    private readonly RunningActions _running = new();

    /// <summary>Marks a search is still adding groups to, holding the first of <paramref name="groups"/>.</summary>
    private DuplicateMarks StillAdding(params DuplicateCandidate[][] groups)
    {
        var result = Result(groups);
        var marks = DuplicateMarks.For(
            result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);
        marks.Add(result.Groups[0]);

        return marks;
    }

    private DuplicateActions Actions(IDuplicateConfirmationPrompt prompt, IRecycleBin? bin = null) =>
        new(_ => Protections(), _ => null, () => prompt, Remover(bin), _running);

    /// <summary>
    /// A rule and a judgement, which every confirmation and removal begins with, refuse to read the
    /// groups while the search may still add one on another thread, and read them once it has ended.
    /// </summary>
    [Fact]
    public async Task NoRuleOrJudgementReadsTheGroupsBeforeTheSearchEnds()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg"), modified: Older), Copy(Path.Combine(Downloads, "a.jpg"), modified: Newer)];
        var marks = StillAdding(files);
        var protections = await Protections();

        Assert.Throws<InvalidOperationException>(() => marks.Run(new MarkingRule.KeepNewest()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => marks.RejudgeAsync(protections));
        Assert.Throws<InvalidOperationException>(() => RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => null));
        // Refused on the calling thread, before the rule goes to another.
        Assert.Throws<InvalidOperationException>(() => { _ = Actions(new FakeDuplicateConfirmation(true)).RunAsync(marks, new MarkingRule.KeepNewest()); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Actions(new FakeDuplicateConfirmation(true)).RemoveAsync(marks, ExploreRemovalMode.RecycleBin));

        marks.Complete();

        Assert.Equal(1, marks.Run(new MarkingRule.KeepNewest()).Marked);
        Assert.Equal([files[0]], (await RemovalConfirmation.ForAsync(marks, protections, ExploreRemovalMode.RecycleBin, _ => null)).Copies);
    }

    /// <summary>Once the search has said it ended, a group arriving after it is a broken contract, never a group added under a rule's feet.</summary>
    [Fact]
    public void NoGroupIsAddedOnceTheSearchHasEnded()
    {
        var late = new[] { Copy(Path.Combine(Documents, "b.jpg")), Copy(Path.Combine(Downloads, "b.jpg")) };
        var result = Result([Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))], late);
        var marks = DuplicateMarks.For(
            result.Finding, _tree.Policy(), _cleans, _tree.Environment, _cloud, _tree.Volumes, _media.Of, _media.Now, FileInformation.Default);
        marks.Add(result.Groups[0]);

        marks.Complete();

        Assert.Throws<InvalidOperationException>(() => marks.Add(result.Groups[1]));
        Assert.Single(marks.Groups);
    }

    /// <summary>
    /// Where no mark stands once the marks are judged again, the user is not shown a dialog to say yes
    /// to, and nothing is removed.
    /// </summary>
    [Fact]
    public async Task NothingIsAskedWhereNoMarkStands()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var prompt = new FakeDuplicateConfirmation(true);

        // The copy that would stay is in the temporary folder by the time the removal is asked for.
        _tree.Environment.WithTempPath(Documents);
        var answer = await Actions(prompt).RemoveAsync(marks, ExploreRemovalMode.RecycleBin);

        Assert.Empty(prompt.Asked);
        Assert.Null(answer.Report);
        Assert.Empty(answer.Confirmation.Copies);
        Assert.True(File.Exists(copy.Path));
    }

    [Fact]
    public async Task ADeclinedRemovalRemovesNothing()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var prompt = new FakeDuplicateConfirmation(false);

        var answer = await Actions(prompt).RemoveAsync(marks, ExploreRemovalMode.Permanent);

        Assert.Equal([copy], Assert.Single(prompt.Asked).Copies);
        Assert.Equal(ExploreRemovalMode.Permanent, prompt.Asked[0].Mode);
        Assert.Null(answer.Report);
        Assert.Equal("Nothing was removed.", answer.Summary);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// A confirmed removal takes what the confirmation listed, and is recorded as running while it
    /// works, so neither closing the window nor elevating ends the process under it, and not
    /// afterwards, so neither is held for good.
    /// </summary>
    [Fact]
    public async Task AConfirmedRemovalIsRecordedAsRunningUntilItReports()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        bool? mayEndWhileRemoving = null;

        var answer = await Actions(
            new FakeDuplicateConfirmation(true),
            FakeRecycleBin.MovingTo(Bin, _ => mayEndWhileRemoving = _running.MayEndProcess)).RemoveAsync(marks, ExploreRemovalMode.RecycleBin);

        Assert.False(mayEndWhileRemoving);
        Assert.True(_running.MayEndProcess);
        Assert.True(Assert.Single(answer.Report!.Copies).Removed);
        Assert.False(File.Exists(copy.Path));
        Assert.True(File.Exists(kept.Path));
        Assert.Equal(answer.Report.Summary, answer.Summary);
    }
}
