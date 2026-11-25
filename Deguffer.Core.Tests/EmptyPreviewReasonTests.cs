using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// Why the Storage preview draws none of its rows. A scan whose locations are clear has nothing left
/// to clean, which is a result to state rather than a filter to undo, and only a scan whose every
/// location is not installed is the filter's doing.
/// </summary>
public sealed class EmptyPreviewReasonTests
{
    [Fact]
    public void NoRowIsNothingScanned() =>
        Assert.Equal(EmptyPreview.NothingScanned, EmptyPreviewReason.Of([]));

    /// <summary>
    /// The state a clean that emptied everything leaves: each location it cleaned is clear, and the
    /// tools this machine lacks are still not installed beside them.
    /// </summary>
    [Fact]
    public void OneClearLocationAmongToolsNotInstalledIsNothingLeftToClean()
    {
        Assert.Equal(
            EmptyPreview.NothingLeftToClean,
            EmptyPreviewReason.Of([FindingStatus.ToolchainMissing, FindingStatus.AlreadyClear, FindingStatus.ToolchainMissing]));
        Assert.Equal(EmptyPreview.NothingLeftToClean, EmptyPreviewReason.Of([FindingStatus.AlreadyClear]));
    }

    /// <summary>A machine with none of the tools never had anything to clean, so saying nothing is left would be untrue.</summary>
    [Fact]
    public void EveryToolNotInstalledIsTheFiltersDoing() =>
        Assert.Equal(
            EmptyPreview.HiddenByFilters,
            EmptyPreviewReason.Of([FindingStatus.ToolchainMissing, FindingStatus.ToolchainMissing]));
}
