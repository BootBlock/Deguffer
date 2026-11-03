using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The progress of a removal that deletes while it is still listing: a fraction of what the plan
/// measured until the walk has counted the tree, then of what the walk counted, and never backwards.
/// </summary>
public sealed class RemovalProgressTests
{
    /// <summary>One report's worth of entries, which is when a report is made.</summary>
    private const int Batch = 256;

    private readonly List<double> _reported = [];

    [Fact]
    public void IsAFractionOfThePlanWhileTheWalkIsStillCounting()
    {
        var progress = new RemovalProgress(new CallbackProgress<double>(_reported.Add), planned: 4 * Batch);

        progress.Found(Batch);
        Finish(progress, Batch);

        Assert.Equal([0.25], _reported);
    }

    [Fact]
    public void IsAFractionOfWhatTheWalkCountedOnceItHasCountedTheTree()
    {
        var progress = new RemovalProgress(new CallbackProgress<double>(_reported.Add), planned: 4 * Batch);

        progress.Found(2 * Batch);
        progress.Counted();
        Finish(progress, Batch);

        Assert.Equal([0.5], _reported);
    }

    /// <summary>
    /// The plan measured less than the walk then found, so the fraction a report would give falls.
    /// The bar is held where it was instead.
    /// </summary>
    [Fact]
    public void NeverMovesBackwardsWhenTheWalkFindsMoreThanThePlan()
    {
        var progress = new RemovalProgress(new CallbackProgress<double>(_reported.Add), planned: 2 * Batch);

        progress.Found(Batch);
        Finish(progress, Batch);
        progress.Found(4 * Batch);
        Finish(progress, Batch);
        Finish(progress, Batch);
        progress.Finished();

        Assert.Equal([0.5, 0.6, 1.0], _reported);
    }

    private static void Finish(RemovalProgress progress, int entries)
    {
        for (var entry = 0; entry < entries; entry++)
        {
            progress.Done();
        }
    }
}
