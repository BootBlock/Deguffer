using System.Numerics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// Where a map's whole picture is on the screen: one scale and one offset, in a property set the
/// compositor reads, and every part of the picture follows by an expression.
///
/// <para>One authority for the placement. The drawings, the outlines and the names are each placed
/// in the picture once, and this places the picture, so they move together by construction rather
/// than by the same transform being copied to each of them at every frame. A move writes these two
/// values and nothing else.</para>
/// </summary>
internal sealed class MapCamera
{
    private readonly ExpressionAnimation _scale;

    private readonly ExpressionAnimation _offset;

    public MapCamera(Compositor compositor)
    {
        Properties = compositor.CreatePropertySet();
        Properties.InsertVector3(nameof(Visual.Scale), Vector3.One);
        Properties.InsertVector3(nameof(Visual.Offset), Vector3.Zero);

        _scale = compositor.CreateExpressionAnimation("camera.Scale");
        _scale.SetReferenceParameter("camera", Properties);
        _offset = compositor.CreateExpressionAnimation("camera.Offset");
        _offset.SetReferenceParameter("camera", Properties);
    }

    /// <summary>
    /// The camera's <c>Scale</c> and <c>Offset</c>, for an expression that combines them with
    /// something else, as the names over the picture do.
    /// </summary>
    public CompositionPropertySet Properties { get; }

    /// <summary>Put the whole picture where <paramref name="camera"/> says.</summary>
    public void Show(MapTransform camera)
    {
        Properties.InsertVector3(nameof(Visual.Scale), new Vector3((float)camera.ScaleX, (float)camera.ScaleY, 1));
        Properties.InsertVector3(nameof(Visual.Offset), new Vector3((float)camera.X, (float)camera.Y, 0));
    }

    /// <summary>Move <paramref name="visual"/> with the camera, on the compositor, from now on.</summary>
    public void Follow(Visual visual)
    {
        visual.StartAnimation(nameof(Visual.Scale), _scale);
        visual.StartAnimation(nameof(Visual.Offset), _offset);
    }

    /// <summary>
    /// Stop <paramref name="visual"/> following the camera, and leave it where
    /// <paramref name="camera"/> put it.
    /// </summary>
    public static void Freeze(Visual visual, MapTransform camera)
    {
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.Offset));

        visual.Scale = new Vector3((float)camera.ScaleX, (float)camera.ScaleY, 1);
        visual.Offset = new Vector3((float)camera.X, (float)camera.Y, 0);
    }
}
