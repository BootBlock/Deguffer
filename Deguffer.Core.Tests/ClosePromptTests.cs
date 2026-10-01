using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the user is asked when they close the window while something runs. It must name what is
/// running, say what closing now would lose, and offer the close they wanted without that loss.
/// </summary>
public sealed class ClosePromptTests
{
    [Fact]
    public void NamesTheOneActionRunning()
    {
        var prompt = ClosePrompt.For([RunningAction.StorageClean]);

        Assert.Equal("Close Deguffer while a clean on the Storage page is running?", prompt.Title);
        Assert.Contains("stops a clean on the Storage page part-way", prompt.Consequence);
        Assert.Equal("Close when it finishes", prompt.CloseWhenDoneLabel);
        Assert.Equal("Keep Deguffer open", prompt.KeepOpenLabel);
    }

    [Fact]
    public void NamesEveryActionRunning()
    {
        var prompt = ClosePrompt.For(
            [RunningAction.StorageClean, RunningAction.ExploreRemoval, RunningAction.Uninstall]);

        Assert.Equal(
            "Close Deguffer while a clean on the Storage page, a removal on the Explore page and an "
            + "uninstall are running?",
            prompt.Title);
        Assert.Equal("Close when they finish", prompt.CloseWhenDoneLabel);
    }

    /// <summary>The loss is what makes the question worth asking, so the sentence must state it.</summary>
    [Fact]
    public void SaysWhatClosingNowWouldLose()
    {
        var prompt = ClosePrompt.For([RunningAction.ExploreRemoval]);

        Assert.Contains("cannot check what was changed or tell you what was done", prompt.Consequence);
        Assert.Contains("close by itself as soon as it finishes", prompt.Consequence);
    }

    [Theory]
    [InlineData(RunningAction.StorageClean, "a clean on the Storage page")]
    [InlineData(RunningAction.ExploreRemoval, "a removal on the Explore page")]
    [InlineData(RunningAction.EntryRemoval, "a removal of installed app entries")]
    [InlineData(RunningAction.BackupRestore, "a restore of a registry backup")]
    [InlineData(RunningAction.Uninstall, "an uninstall")]
    public void EveryActionHasAName(RunningAction action, string name)
    {
        Assert.Contains(name, ClosePrompt.For([action]).Title);
    }

    [Fact]
    public void RefusesToAskAboutNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClosePrompt.For([]));
    }
}
