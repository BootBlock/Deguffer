using Deguffer.App.Controls;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// The compositor works out the same few expressions for a map's names on every frame of a pan or a
/// zoom whatever their number: three that carry them all with the picture, and one for each fade step
/// a name is in. With an expression or two for each name, a maximised map's two thousand names cost
/// the compositor most of each frame.
///
/// <para>Starting each expression is also what the compositor parses it at, so a test here refuses an
/// expression the compositor would not read.</para>
/// </summary>
public sealed class NameLayerTests
{
    /// <summary>How many expressions carry every name with the picture: where, how magnified and how opaque.</summary>
    private const int Carrying = 3;

    [Theory]
    [InlineData(10)]
    [InlineData(2000)]
    public void NamesFadingAlikeCostOneExpressionWhateverTheirNumber(int count)
    {
        var running = CompositionThread.Run(() =>
        {
            var (layer, compositor) = Layer();

            for (var i = 0; i < count; i++)
            {
                // Fades a sliver apart, as no two names' are exactly alike, all within one step.
                var across = new LabelFade(0.5f - (i * 1e-6f), 0.6f);
                var down = new LabelFade(0.25f - (i * 1e-6f), 0.3f);

                layer.Fades.Join(compositor.CreateSpriteVisual(), across, down);
            }

            layer.Fades.Settle();

            return layer.Running;
        });

        Assert.Equal(Carrying + 1, running);
    }

    /// <summary>
    /// Names in different fade steps fade apart, one expression for each step in use, and a step its
    /// last name leaves stops, as the names put away when a drawing has fewer leave theirs.
    /// </summary>
    [Fact]
    public void EachStepInUseCostsOneExpressionAndAnEmptiedStepStops()
    {
        var (three, moved, left) = CompositionThread.Run(() =>
        {
            var (layer, compositor) = Layer();
            var names = new[] { compositor.CreateSpriteVisual(), compositor.CreateSpriteVisual(), compositor.CreateSpriteVisual() };
            var whole = LabelFade.AtStep(-1);

            layer.Fades.Join(names[0], LabelFade.AtStep(-10), whole);
            layer.Fades.Join(names[1], LabelFade.AtStep(-20), whole);
            layer.Fades.Join(names[2], whole, LabelFade.AtStep(-10));
            layer.Fades.Settle();
            var three = layer.Running;

            layer.Fades.Join(names[1], LabelFade.AtStep(-10), whole);
            layer.Fades.Settle();
            var moved = layer.Running;

            NameFades.Leave(names[0]);
            NameFades.Leave(names[1]);
            layer.Fades.Settle();

            return (three, moved, layer.Running);
        });

        Assert.Equal(Carrying + 3, three);
        Assert.Equal(Carrying + 2, moved);
        Assert.Equal(Carrying + 1, left);
    }

    /// <summary>A name moved to another step is in that step's visual alone, so it fades only as that step does.</summary>
    [Fact]
    public void ANameIsInOneStepAtATime()
    {
        var (sameParent, parents) = CompositionThread.Run(() =>
        {
            var (layer, compositor) = Layer();
            var name = compositor.CreateSpriteVisual();
            var other = compositor.CreateSpriteVisual();

            layer.Fades.Join(name, LabelFade.AtStep(-10), LabelFade.AtStep(-1));
            layer.Fades.Join(other, LabelFade.AtStep(-10), LabelFade.AtStep(-1));
            var together = name.Parent == other.Parent;

            layer.Fades.Join(name, LabelFade.AtStep(-30), LabelFade.AtStep(-1));
            layer.Fades.Settle();

            return (together, name.Parent != other.Parent && ((ContainerVisual)other.Parent).Children.Count == 1);
        });

        Assert.True(sameParent);
        Assert.True(parents);
    }

    private static (NameLayer Layer, Compositor Compositor) Layer()
    {
        var compositor = CompositionThread.Compositor;
        var camera = compositor.CreatePropertySet();
        camera.InsertVector3(nameof(Visual.Scale), System.Numerics.Vector3.One);
        camera.InsertVector3(nameof(Visual.Offset), System.Numerics.Vector3.Zero);

        return (new NameLayer(compositor, camera, compositor.CreateContainerVisual()), compositor);
    }
}
