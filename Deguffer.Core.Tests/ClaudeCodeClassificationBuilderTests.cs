using Deguffer.Core.Execution;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder offered once it is old enough is decided only when its age has been read, with every
/// other folder its kind offered, in one step.
/// </summary>
public sealed class ClaudeCodeClassificationBuilderTests
{
    /// <summary>
    /// A kind that offered a folder and never dated it would build a classification without it, which
    /// reads as a folder that was never there. Building refuses instead.
    /// </summary>
    [Fact]
    public void AFolderOfferedAndNeverDatedCannotBeBuilt()
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        sorting.OfferFolderOnceOldEnough(@"C:\Users\testuser\.claude\session-env\a", "Ended", isLeftover: true, NeverChecked.Instance);

        Assert.Throws<InvalidOperationException>(sorting.Build);
    }

    private sealed class NeverChecked : IUseCheck
    {
        public static readonly NeverChecked Instance = new();

        public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct) => [];
    }
}
