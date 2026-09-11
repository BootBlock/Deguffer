using Deguffer.Core.Choosing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A checkbox that stands for several items. The dangerous direction is a click that adds items to a
/// run, so the mixed state is the case that matters.
/// </summary>
public class ItemSelectionTests
{
    [Fact]
    public void IsTickedWhenEveryItemThatCanBeTickedIs()
    {
        // The third item is kept, or has nothing to reclaim: nobody can tick it, so it cannot hold the
        // heading short of ticked.
        Assert.True(ItemSelection.StateOf([(true, true), (true, true), (false, false)]));
    }

    [Fact]
    public void IsClearWhenNoItemIsTicked()
    {
        Assert.False(ItemSelection.StateOf([(false, true), (false, true)]));
    }

    [Fact]
    public void IsMixedWhenSomeAreTicked()
    {
        Assert.Null(ItemSelection.StateOf([(true, true), (false, true)]));
    }

    [Fact]
    public void IsClearWhenNothingItCoversCanBeTicked()
    {
        Assert.False(ItemSelection.StateOf([(false, false), (false, false)]));
        Assert.False(ItemSelection.StateOf([]));
    }

    /// <summary>
    /// Only a clear checkbox ticks. A mixed one clears, because "some of these" is not an instruction
    /// to delete the rest of them.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void AClickTicksOnlyFromClear(bool? state, bool written)
    {
        Assert.Equal(written, ItemSelection.ValueForClick(state));
    }
}
