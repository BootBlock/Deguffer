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
}
