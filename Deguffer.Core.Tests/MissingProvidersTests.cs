using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which locations the list of what to scan marks as not found. The mark has to agree with the row's
/// own "Not installed", and has to survive a later scan that did not measure the location.
/// </summary>
public sealed class MissingProvidersTests
{
    private static readonly FakeCleanupProvider Steam = new("steam");
    private static readonly FakeCleanupProvider Temp = new("temp");

    [Fact]
    public void NothingIsMarkedBeforeAScan()
    {
        Assert.False(new MissingProviders().WasMissing(Steam));
    }

    [Fact]
    public void MarksAProviderTheScanFoundNotInstalled()
    {
        var missing = new MissingProviders();

        missing.Record(Steam, FindingStatus.ToolchainMissing);
        missing.Record(Temp, FindingStatus.AlreadyClear);

        Assert.True(missing.WasMissing(Steam));
        Assert.False(missing.WasMissing(Temp));
    }

    /// <summary>
    /// A location waiting on a source folder is absent in the same way, and is the one the user can
    /// do something about. Marking it would tell them to leave it unticked.
    /// </summary>
    [Fact]
    public void DoesNotMarkALocationWaitingOnASourceFolder()
    {
        var missing = new MissingProviders();

        missing.Record(Steam, FindingStatus.AwaitingSourceFolders);

        Assert.False(missing.WasMissing(Steam));
    }

    /// <summary>A tool installed since is found by the next scan that measures it, and the mark goes.</summary>
    [Fact]
    public void ALaterScanThatFindsItTakesTheMarkAway()
    {
        var missing = new MissingProviders();
        var changes = new List<string>();
        missing.Changed += (_, provider) => changes.Add(provider.Id);

        missing.Record(Steam, FindingStatus.ToolchainMissing);
        missing.Record(Steam, FindingStatus.ToolchainMissing);
        missing.Record(Steam, FindingStatus.ReadyToClean);

        Assert.False(missing.WasMissing(Steam));

        // Once each way, and nothing for the repeat, so a list row is told only when its mark moves.
        Assert.Equal(["steam", "steam"], changes);
    }
}
