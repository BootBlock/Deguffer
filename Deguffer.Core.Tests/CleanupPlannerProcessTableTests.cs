using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A planning pass starts reading the process table at its top, so the rows that ask it find the
/// reading under way rather than paying for it inside their own slots.
/// </summary>
public sealed class CleanupPlannerProcessTableTests
{
    /// <summary>
    /// The row never asks the inspector, and waits to see the table opened. Only a pass that starts the
    /// read itself opens it.
    /// </summary>
    [Fact]
    public async Task APassReadsTheProcessTableWithoutARowAsking()
    {
        using var opened = new ManualResetEventSlim();
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess { Id = 4700, WhileOpening = opened.Set });
        var inspector = new LiveTreeInspector(calls, new FakeVolumeInventory());
        var readWhilePlanning = false;
        var row = new FakeCleanupProvider("row") { WhilePlanning = () => readWhilePlanning = opened.Wait(TimeSpan.FromSeconds(10)) };

        await new CleanupPlanner([row], liveTrees: inspector).PlanAllAsync();

        Assert.True(readWhilePlanning);
        Assert.Equal(1, calls.ListCount);
    }
}
