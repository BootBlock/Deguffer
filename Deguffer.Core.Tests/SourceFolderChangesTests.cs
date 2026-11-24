using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows a newly approved source folder sends back to be planned. Too few leaves a row asking for
/// a folder the user has just given. Too many measures again every row the folder cannot change.
/// </summary>
public sealed class SourceFolderChangesTests
{
    /// <summary>
    /// Every row the scan found waiting is planned again, in the order shown, and a row that was not
    /// waiting is left as it stands.
    /// </summary>
    [Fact]
    public void EveryRowWaitingForAFolderIsPlannedAgainAndNoOther()
    {
        var npm = new Finding(new FakeCleanupProvider("npm"), IsPresent: true, Plan: null);
        var builds = new Finding(new FakeCleanupProvider("builds"), IsPresent: false, Plan: null, AwaitingSourceFolders: true);
        var dotnet = new Finding(new FakeCleanupProvider("dotnet"), IsPresent: true, Plan: null, AwaitingSourceFolders: true);

        var stale = SourceFolderChanges.Stale([builds, npm, dotnet]);

        Assert.Equal(["builds", "dotnet"], stale.Select(provider => provider.Id));
    }

    /// <summary>
    /// The finding is asked, not the provider: once the folder is approved the provider says it is no
    /// longer waiting, and its row is the one that most needs planning again.
    /// </summary>
    [Fact]
    public void TheFindingDecidesNotTheProviderWhoseAnswerTheFolderChanged()
    {
        var provider = new FakeCleanupProvider("builds") { IsAwaitingSourceFolders = false };
        var waiting = new Finding(provider, IsPresent: false, Plan: null, AwaitingSourceFolders: true);

        Assert.Same(provider, Assert.Single(SourceFolderChanges.Stale([waiting])));
    }
}
