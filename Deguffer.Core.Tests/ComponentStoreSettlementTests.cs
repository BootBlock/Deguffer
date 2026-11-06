using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The record of what each of Windows' component store cleanups left. A record read wrongly in one
/// direction hides a cleanup that would free gigabytes, so anything this store would not have written is
/// read as no record at all.
/// </summary>
public sealed class ComponentStoreSettlementTests : IDisposable
{
    private static readonly ComponentStoreState Store = new(18_190_000_000, 16_900_000_000, 8_460_000_000, 8_450_000_000, 0, 2);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly string _file;

    public ComponentStoreSettlementTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _file = Path.Combine(_environment.LocalAppData, "Deguffer", "component-store.json");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ARecordIsReadBackByTheNextSession()
    {
        new ComponentStoreSettlement(_environment).Record(ComponentStoreCommand.Cleanup, Store);

        var next = new ComponentStoreSettlement(_environment);

        Assert.True(next.HasSettled(ComponentStoreCommand.Cleanup, Store));
        Assert.False(next.HasSettled(ComponentStoreCommand.ResetBase, Store));
    }

    /// <summary>Every figure counts: a store that differs in any of them is not the one the command left.</summary>
    [Fact]
    public void AStoreThatDiffersInAnyFigureIsNotSettled()
    {
        var settlement = new ComponentStoreSettlement(_environment);
        settlement.Record(ComponentStoreCommand.ResetBase, Store);

        ComponentStoreState[] changed =
        [
            Store with { ExplorerSize = Store.ExplorerSize + 10_000_000 },
            Store with { ActualSize = Store.ActualSize + 10_000_000 },
            Store with { SharedWithWindows = Store.SharedWithWindows + 10_000_000 },
            Store with { BackupsAndDisabledFeatures = Store.BackupsAndDisabledFeatures + 10_000_000 },
            Store with { CacheAndTemporaryData = 1 },
            Store with { ReclaimablePackages = 3 },
        ];

        Assert.True(settlement.HasSettled(ComponentStoreCommand.ResetBase, Store));
        Assert.All(changed, state => Assert.False(settlement.HasSettled(ComponentStoreCommand.ResetBase, state)));
    }

    /// <summary>
    /// A file this store would not write is no record, never part of one. Each case but the torn and
    /// the null one carries a valid entry for the cleanup beside its fault, so it passes only where the
    /// whole file is refused.
    /// </summary>
    [Theory]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1")]
    [InlineData("{ \"format\": 2, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 } } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0 } } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 }, \"Repair\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 } } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 }, \"1\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 } } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 }, \"ResetBase\": { \"explorerSize\": 1, \"actualSize\": -1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0 } } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": null } }")]
    [InlineData("{ \"format\": 1, \"left\": { \"Cleanup\": { \"explorerSize\": 1, \"actualSize\": 1, \"sharedWithWindows\": 0, \"backupsAndDisabledFeatures\": 0, \"cacheAndTemporaryData\": 0, \"reclaimablePackages\": 0, \"pendingReboot\": true } } }")]
    public void AFileThisStoreWouldNotWriteIsNoRecord(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, contents);

        var settlement = new ComponentStoreSettlement(_environment);

        Assert.False(settlement.HasSettled(ComponentStoreCommand.Cleanup, new ComponentStoreState(1, 1, 0, 0, 0, 0)));
    }

    /// <summary>The file holds DISM's figures and nothing that names the machine or its user.</summary>
    [Fact]
    public void TheFileHoldsOnlyTheFigures()
    {
        new ComponentStoreSettlement(_environment).Record(ComponentStoreCommand.ResetBase, Store);

        var written = File.ReadAllText(_file);

        Assert.Contains("\"ResetBase\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Cleanup\"", written, StringComparison.Ordinal);
        Assert.Contains("8450000000", written, StringComparison.Ordinal);
        Assert.DoesNotContain(_temp.Path, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\", written, StringComparison.Ordinal);
    }
}
