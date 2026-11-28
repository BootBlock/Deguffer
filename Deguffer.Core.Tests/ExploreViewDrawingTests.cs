using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

public sealed class ExploreViewDrawingTests
{
    /// <summary>
    /// The three pictures fill the window and the rest keep to the reading width, so a view that
    /// lists rows read across a wide screen is the defect this guards against.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap, true)]
    [InlineData(ExploreView.Icicle, true)]
    [InlineData(ExploreView.Sunburst, true)]
    [InlineData(ExploreView.List, false)]
    [InlineData(ExploreView.Tree, false)]
    [InlineData(ExploreView.Files, false)]
    public void OnlyThePicturesDrawAPicture(ExploreView view, bool draws) =>
        Assert.Equal(draws, view.DrawsPicture());

    /// <summary>Every layout has a name of its own, so no two items in a Layout box read alike.</summary>
    [Fact]
    public void EveryLayoutHasANameOfItsOwn()
    {
        var names = Enum.GetValues<ExploreView>().Select(view => view.DisplayName()).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    /// <summary>
    /// The Memory page offers the tree and not the Files layout, each layout at its own place, and a
    /// layout it does not offer is placed where the treemap is rather than past the box's items.
    /// </summary>
    [Fact]
    public void TheMemoryPageOffersItsOwnLayouts()
    {
        Assert.Contains(ExploreView.Tree, MemoryChoice.Views);
        Assert.DoesNotContain(ExploreView.Files, MemoryChoice.Views);

        for (var i = 0; i < MemoryChoice.Views.Count; i++)
        {
            Assert.Equal(i, MemoryChoice.IndexOf(MemoryChoice.Views[i]));
        }

        Assert.Equal(0, MemoryChoice.IndexOf(ExploreView.Files));
        Assert.Equal(0, MemoryChoice.IndexOf((ExploreView)9));
    }
}
