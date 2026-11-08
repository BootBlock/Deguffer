using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>How a run's unset variables combine.</summary>
public sealed class ToolRunTests
{
    [Fact]
    public void UnsettingAddsToWhatTheRunAlreadyUnsetsWithoutRegardToCase()
    {
        var run = ToolRun.Query
            .Without(new HashSet<string> { "GIT_DIR" })
            .Without(new HashSet<string> { "git_dir", "GIT_INDEX_FILE" });

        Assert.Equal(2, run.Unset.Count);
        Assert.Contains("git_index_file", run.Unset);
        Assert.Contains("Git_Dir", run.Unset);
    }

    /// <summary>
    /// A command step that unsets nothing is run as the run it always was, so it compares equal to
    /// one, as the executor's tests rely on.
    /// </summary>
    [Fact]
    public void UnsettingNothingNewLeavesTheRunAsItWas()
    {
        var run = ToolRun.Command(CancelledCommand.Ended).Without(new HashSet<string> { "GIT_DIR" });

        Assert.Same(run, run.Without(new HashSet<string> { "git_dir" }));
        Assert.Equal(ToolRun.Command(CancelledCommand.Ended), ToolRun.Command(CancelledCommand.Ended).Without(new HashSet<string>()));
    }
}
