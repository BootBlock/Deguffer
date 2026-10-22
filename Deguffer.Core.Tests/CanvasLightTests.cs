using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// When the pointer's light is shaded onto a canvas: never in the commit that shaded the canvas
/// whole, which left the rest of the surface empty on screen, and once a commit however often the
/// pointer moves, over where the light was and where it is.
/// </summary>
public sealed class CanvasLightTests
{
    private static readonly CushionQuads Quads = new([], 1000, 1000, _ => default, ShapeFinish.Cushioned);

    private static readonly PointerLight Here = new(100, 100, 10);

    private static readonly PointerLight There = new(130, 105, 10);

    /// <summary>
    /// A light wanted straight after the canvas is shaded whole asks for no commit of its own, and is
    /// shaded only once the whole shading's commit is done.
    /// </summary>
    [Fact]
    public void ALightWaitsForTheCommitOfTheWholeShading()
    {
        var light = new CanvasLight();
        var shaded = light.Shaded(Quads, default);

        Assert.Null(light.Want(Here));

        var shading = light.Committed(shaded);

        Assert.Equal(new CanvasLightShading(Quads, default, Here.Lit(1000, 1000)!.Value, Here), shading);
    }

    /// <summary>
    /// A canvas shaded whole after a light was asked for is not lit on the light's commit, which may be
    /// the one that holds the whole shading, but on its own.
    /// </summary>
    [Fact]
    public void AWholeShadingSupersedesALightAskedForBeforeIt()
    {
        var light = new CanvasLight();
        light.Committed(light.Shaded(Quads, default));

        var asked = light.Want(Here);
        var shaded = light.Shaded(Quads, default);

        Assert.NotNull(asked);
        Assert.Null(light.Committed(asked!.Value));
        Assert.Equal(Here, light.Committed(shaded)?.Light);
    }

    /// <summary>
    /// The pointer moving several times before a commit is shaded once, under the last light, over
    /// one rectangle round where the light was and where it is now.
    /// </summary>
    [Fact]
    public void MovesBeforeACommitAreShadedOnce()
    {
        var light = new CanvasLight();
        light.Committed(light.Shaded(Quads, default));
        light.Committed(light.Want(Here)!.Value);

        var asked = light.Want(new PointerLight(115, 100, 10));

        Assert.NotNull(asked);
        Assert.Null(light.Want(There));

        var shading = light.Committed(asked!.Value);

        Assert.Equal(PointerLight.Between(Here, There, 1000, 1000), shading?.Region);
        Assert.Equal(There, shading?.Light);
    }

    /// <summary>A light taken away is shaded off over what it lit, with no light.</summary>
    [Fact]
    public void ALightTakenAwayIsShadedOff()
    {
        var light = new CanvasLight();
        light.Committed(light.Shaded(Quads, default));
        light.Committed(light.Want(Here)!.Value);

        var shading = light.Committed(light.Want(null)!.Value);

        Assert.Equal(Here.Lit(1000, 1000), shading?.Region);
        Assert.Null(shading?.Light);
    }

    /// <summary>A light already where it is wanted asks for nothing.</summary>
    [Fact]
    public void ALightAlreadyThereAsksForNothing()
    {
        var light = new CanvasLight();
        light.Committed(light.Shaded(Quads, default));
        light.Committed(light.Want(Here)!.Value);

        Assert.Null(light.Want(Here));
    }

    /// <summary>
    /// A canvas painted on the CPU, or one let go of, has nothing to shade a light from, even where a
    /// commit was asked for before it was let go of.
    /// </summary>
    [Fact]
    public void NothingIsLitWithoutACanvasShadedOnTheGpu()
    {
        var light = new CanvasLight();

        Assert.Null(light.Want(Here));

        var shaded = light.Shaded(Quads, default);
        light.Want(Here);
        light.Forget();

        Assert.Null(light.Committed(shaded));
        Assert.Null(light.Want(Here));
    }
}
