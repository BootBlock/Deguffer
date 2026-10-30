using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What a page's map tells its view-model: what the pointer is over, a shape opened or picked, and a
/// step back up. Explore's view-model and Memory's both answer it, so the two pages wire their maps
/// the same way (<see cref="Views.MapWiring"/>).
/// </summary>
public interface IMapNavigator
{
    /// <summary>The pointer moved over <paramref name="hit"/>, or over nothing.</summary>
    void Hover(ExploreHit? hit);

    /// <summary>Open the shape of <paramref name="node"/>.</summary>
    void Descend(int node);

    /// <summary>Go up to the folder or part above the one on screen, where there is one.</summary>
    void Ascend();

    /// <summary>The reader picked the shape of <paramref name="node"/>, or a place with no shape.</summary>
    void Pick(int? node);

    /// <summary>What to say about the shape of <paramref name="node"/> in <paramref name="tree"/>.</summary>
    MapShapeFacts? ShapeFacts(ISizedTree tree, int node);
}
